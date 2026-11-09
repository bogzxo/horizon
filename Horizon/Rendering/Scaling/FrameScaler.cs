using System.Numerics;

using Horizon.Graphics;
using Horizon.Logging;

namespace Horizon.Rendering.Scaling;

/// <summary>
/// The engine's render scale, one per window (<c>WindowManager.Scaling</c>). With it on, the scene is drawn into a
/// target smaller than the window and blown up into it by an <see cref="Upscaler"/>, CAS unless told otherwise. Nothing
/// that draws has to know. The device has the target stand in for the window (see
/// <see cref="GraphicsDevice.WindowTargetSize"/>), so whatever binds the window draws small, a renderer that follows
/// the window is the scene's size, and the camera, the UI's layout and the pointer stay the window's.
/// <para>
/// A frame is two parts. First the scene, drawn small. Then, with <see cref="RenderComposition.NativeOverlay"/>,
/// the first UI drawn straight into the window (not inside of a renderer) calls <see cref="BeginOverlay"/>, the scene
/// is blown up into the window there and then, and everything after is drawn at full size in the order it always
/// was, nothing dropped and nothing moved round. With <see cref="RenderComposition.WholeFrame"/> there is no second
/// part, the UI is drawn small with the rest and the whole frame is blown up at the end. A frame nobody overlaid is
/// blown up at its end either way, as is one somebody reads back (a screenshot), first.
/// </para>
/// <para>
/// Settings take at the start of the next frame, from any thread, and are checked over first (see
/// <see cref="RenderScaleSettings.Sanitized"/>). At a scale of 1 there is no target and no pass, the frame is what it
/// always was. With no surface nothing stands in for the window and nothing is blown up, the scene's size is still
/// what renderers that follow the window are told, so they don't make their targets anew for a frame without one.
/// </para>
/// </summary>
public sealed partial class FrameScaler : IDisposable
{
    private readonly Lock settingsLock = new();
    private RenderScaleSettings settings = RenderScaleSettings.Native;
    private int settingsVersion;

    // What the plan was last worked out from, so it's only worked out again when one of them changes
    private int plannedVersion = -1;
    private uint plannedWidth, plannedHeight, plannedMax;
    private float plannedDensity;
    private volatile RenderScalePlan plan = RenderScalePlan.NativeFor(0, 0);

    // The scene's target, made while the plan is active, and the size of the window it's blown up into
    private RenderTarget? scene;
    private bool inScene;
    private uint outputWidth, outputHeight;
    private GraphicsDevice? device;

    // The custom upscaler of the settings the frame took, for the whole of the frame
    private ISpatialUpscaler? frameCustom;

    /// <summary>
    /// What it's asked to do. From any thread, taken at the start of the next frame. Reads back what was last set,
    /// which isn't necessarily in use yet, see <see cref="Plan"/> for that.
    /// </summary>
    public RenderScaleSettings Settings
    {
        get
        {
            lock (settingsLock) return settings;
        }
        set
        {
            int version;
            lock (settingsLock)
            {
                settings = value;
                version = ++settingsVersion;
            }

            SettingsChanged?.Invoke(value);
        }
    }

    /// <summary>Counts every time the settings are set, for a picker that shows them to see somebody else changed them. From any thread.</summary>
    public int Version => Volatile.Read(ref settingsVersion);

    /// <summary>Called on whichever thread set the settings, with what they were set to.</summary>
    public event Action<RenderScaleSettings>? SettingsChanged;

    /// <summary>What the last frame did about its size. From any thread.</summary>
    public RenderScalePlan Plan => plan;

    /// <summary>Whether the frame is drawing its scene small right now. Render thread.</summary>
    public bool InScene => inScene;

    /// <summary>
    /// How big what binding the window draws into is right now. The scene's size while it's drawn small (and for a
    /// frame without a surface), the window's once the overlay began or nothing is scaled. Render thread.
    /// </summary>
    public Vector2 TargetSize
    {
        get
        {
            RenderScalePlan now = plan;
            if (inScene || (now.Active && device is { HasWindowImage: false }))
                return new Vector2(now.SceneWidth, now.SceneHeight);
            return new Vector2(outputWidth, outputHeight);
        }
    }

    /// <summary>Called when the plan changes, on the render thread, with the new one. For overlays and tests.</summary>
    public event Action<RenderScalePlan>? PlanChanged;

    /// <summary>
    /// Starts a frame, after the device began it. Takes the settings, works the plan out if anything it goes by
    /// changed, makes the scene's target to fit and has it stand in for the window. Render thread.
    /// </summary>
    internal void BeginFrame(GraphicsDevice graphics, uint width, uint height, float density)
    {
        device = graphics;
        inScene = false;

        RenderScaleSettings now;
        int version;
        lock (settingsLock)
        {
            now = settings;
            version = settingsVersion;
        }

        // What the frame ends up as is the swapchain when there is one, it's what is drawn into
        if (graphics.HasWindowImage)
        {
            width = graphics.SwapchainWidth;
            height = graphics.SwapchainHeight;
        }

        outputWidth = width;
        outputHeight = height;

        uint most = graphics.MaxTextureSize;
        if (Interlocked.Exchange(ref probeRequested, 0) != 0)
            ProbeAll();

        if (version != plannedVersion || width != plannedWidth || height != plannedHeight || most != plannedMax || !density.Equals(plannedDensity) || availabilityVersion != plannedAvailability)
        {
            RenderScalePlan made = Usable(now, RenderScalePlan.For(now, width, height, density, most), width, height, density, most);
            (plannedVersion, plannedWidth, plannedHeight, plannedMax, plannedDensity, plannedAvailability) = (version, width, height, most, density, availabilityVersion);

            if (made != plan)
            {
                // A stretch ahead of CAS is only kept while a plan has one
                if (!made.HasPrepass)
                    ReleasePrepass();
                if (made.Effective != Upscaler.Fsr1)
                    ReleaseEasu();

                bool said = made.Active || plan.Active;
                plan = made;
                if (said)
                    Log.Info($"[RenderScale] {Describe(made)}");
                PlanChanged?.Invoke(made);
            }
        }

        frameCustom = now.Custom;

        RenderScalePlan current = plan;
        if (!current.Active)
        {
            Release();
            return;
        }

        if (!graphics.HasWindowImage)
            return;

        if (!Fit(current.SceneWidth, current.SceneHeight))
            return;

        graphics.StandInForWindow(scene);
        CameraBlock.ChangeViewport(new Vector2(current.SceneWidth, current.SceneHeight));
        inScene = true;
        scaledThisFrame = true;
    }

    /// <summary>
    /// Ends the scene part of the frame for a UI that's drawn at the window's own pixels, see the class remarks. Does
    /// nothing outside of the scene part or when the whole frame is drawn small. Render thread.
    /// </summary>
    public void BeginOverlay()
    {
        if (!inScene || plan.Composition == RenderComposition.WholeFrame)
            return;

        overlaid = true;
        FinishScene();
    }

    /// <summary>
    /// Blows the scene up into the window now, whatever the composition, so what's in the window is the frame as far
    /// as it has been drawn (for a read back). Everything after is drawn at full size. Render thread.
    /// </summary>
    public void FinishScene()
    {
        if (!inScene || device is not { } graphics)
            return;

        inScene = false;
        Upscale(graphics);
        CameraBlock.ChangeViewport(new Vector2(outputWidth, outputHeight));
    }

    /// <summary>Ends the frame, blowing the scene up if nobody did, before the device ends it. Render thread.</summary>
    internal void EndFrame()
    {
        FinishScene();
        device?.StandInForWindow(null);
        LastFrame = !scaledThisFrame ? ScaledFrame.Native : overlaid ? ScaledFrame.Overlaid : ScaledFrame.Whole;
        scaledThisFrame = false;
        overlaid = false;
    }

    // Whether this frame drew its scene small, and whether a UI ended that scene for its overlay
    private bool scaledThisFrame, overlaid;

    /// <summary>How the last frame came out, for the checks and the overlays. From any thread.</summary>
    public ScaledFrame LastFrame { get; private set; }

    /// <summary>Says what a plan does, for the log.</summary>
    public static string Describe(RenderScalePlan made)
    {
        if (!made.Active)
            return $"Native, {made.OutputWidth}x{made.OutputHeight} drawn as it is ({made.Policy}, density {made.Density:0.###}).";

        var (dpWidth, dpHeight) = made.LogicalSize;
        string fallback = made.Fallback is null ? "" : $" {char.ToUpperInvariant(made.Fallback[0])}{made.Fallback[1..]}.";
        return $"{made.SceneWidth}x{made.SceneHeight} blown up to {made.OutputWidth}x{made.OutputHeight} ({made.EffectiveScale:0.###} of it, {made.Policy} asked {made.RequestedScale:0.###}) with {made.Effective}{(made.HasPrepass ? $" after a bilinear stretch to {made.PrepassWidth}x{made.PrepassHeight} (past {RenderScalePlan.NameOf(made.Effective)}'s 4 times the area in one go)" : "")}, sharpness {made.Sharpness:0.##}, {made.Composition}, density {made.Density:0.###} so {dpWidth:0}x{dpHeight:0} dp.{fallback}";
    }

    /// <summary>
    /// Helper method to have the scene's target be a size. Made anew when the size changes, the old one goes once
    /// the GPU is done with it.
    /// </summary>
    private bool Fit(uint width, uint height)
    {
        if (scene is { IsValid: true } && scene.Width == width && scene.Height == height)
            return true;

        Release();

        // The engine's own for as long as it lives, never whichever scene happened to be drawing
        using var global = Horizon.Content.AssetScope.EnterGlobal();
        bool made = Engine.GameEngine.Instance.ObjectManager.RenderTargets.TryCreate(
            RenderTargetDescription.Color(width, height, TextureDefinition.RgbaUnsignedByte),
            out var result);
        if (!made)
        {
            Log.Error($"[RenderScale] A scene target of {width} by {height} couldn't be made, the frame is drawn at full size: {result.Message}");
            return false;
        }

        scene = result.Asset;
        scene.Name = "scaled scene";
        return true;
    }

    private void Release()
    {
        ReleasePrepass();
        ReleaseEasu();
        if (scene is null)
            return;

        device?.StandInForWindow(null);
        scene.Dispose();
        scene = null;
    }

    /// <summary>Frees the scene's target and the passes. Render thread, with the device still there.</summary>
    public void Dispose()
    {
        inScene = false;
        Release();
        DisposePasses();
    }
}
