using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.UIX.Components;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;

namespace Horizon.Rendering.UIX;

/// <summary>
/// Runs a UI: owns the skin and the modules, routes the pointer to the component under it, and draws
/// everything on top of the scene.
/// The work is split the way the engine splits it. Input, layout and painting happen in
/// <see cref="UpdateState"/> on the logic thread and produce a list of quads; <see cref="Render"/>
/// only hands the latest list to the sprite renderer, so components never touch the GPU.
/// <para>
/// Drawn alongside the simulation (see <see cref="RenderFrame.IsDecoupled"/>) the list that was painted last is
/// published at the end of every tick (<see cref="Capture"/>), and every frame draws the UI at the moment between the
/// last two ticks it shows: what slides, pops or is tweened goes the same distance for the same time from frame to frame
/// at any frame rate, however the ticks and the frames line up. Quads that are the same in both lists but for where they
/// are, how big and what colour are blended; whatever changed in steps (the text, which art) changes at the moment it
/// changed. A UI that is drawn by hand and never published is drawn from its newest list, as it always was.
/// </para>
/// <para>
/// A UI can have effects of its own, see <see cref="PostProcessing"/>: with a
/// <see cref="MotionBlurEffect"/> what slides, pops or is tweened about is smeared along the way it goes.
/// </para>
/// </summary>
public partial class UICompositor : GameComponent, IDisposable
{
    /// <summary>Where the skin every UI has unless it asks for another one is kept.</summary>
    public const string DEFAULT_SKIN_DIRECTORY = "Assets/uix/dead_revolver/";

    /// <summary>The definition of that skin.</summary>
    public const string DEFAULT_SKIN_FILE = "skin.hor";

    // How long (in milliseconds) the UI can go without an update before what moved in the meantime is not motion
    private const long MOTION_BREAK = 100;

    internal readonly Camera2D viewportCamera;
    private string skinDirectory;
    private string skinFile;
    private string? skinTheme;

    // For the layout debugger. Where the pointer was last, and when the UI was last updated.
    private Vector2 lastPointer;
    private long lastUpdate;

    /// <summary>Where the pointer was and whether it was down as of the last update, in the camera's world space.</summary>
    public UIPointer Pointer { get; private set; }

    /// <summary>
    /// The size of the screen the UI was laid out for. Set, the whole UI is scaled so that a screen of that
    /// size just fits into whatever the camera sees. A layout made for 1600 by 900 comes out twice as big in a
    /// window of 3200 by 1800, and has the same room to lay itself out in either way. Where the window is
    /// wider or taller than the design the UI gets the extra room, it is never cut off.
    /// Left unset the UI is laid out against the window as it is, a unit per pixel.
    /// </summary>
    public Vector2? DesignSize { get; set; }

    /// <summary>
    /// Makes the whole UI bigger or smaller by a factor, on top of what <see cref="DesignSize"/> does. For a
    /// setting that lets the player decide how big they want things.
    /// </summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>
    /// How much bigger than it was laid out the UI is drawn right now: <see cref="Scale"/> times whatever
    /// fitting the <see cref="DesignSize"/> comes to. A unit of the layout is this many units of the camera.
    /// </summary>
    public float UIScale { get; private set; } = 1.0f;

    // Scrolling that was asked for with Scroll and hasn't been handed out yet.
    private float pendingScroll;

    /// <summary>
    /// Scrolls whatever is under the pointer as if the mouse wheel had been turned, for input that isn't a
    /// wheel. The stick of a gamepad, or a test. Safe from any thread, it happens on the next update.
    /// </summary>
    /// <param name="notches">How far, a notch of the wheel being 1. Positive is up.</param>
    public void Scroll(float notches)
    {
        float before;
        do
        {
            before = pendingScroll;
        } while (Interlocked.CompareExchange(ref pendingScroll, before + notches, before) != before);
    }

    // A skin asked for with SetSkin, waiting for the GL thread to load it.
    private (string Directory, string File, string? Theme)? requestedSkin;
    private readonly Lock skinLock = new();

    private readonly UIRenderer renderer = new();

    /// <summary>The quads that are drawn, as they were last uploaded. For tests that look at what is on screen, render thread.</summary>
    internal ReadOnlySpan<SpriteItem> Drawn => renderer.Uploaded;

    // Where the UI is drawn while it has effects that are on, rather than straight over the scene
    private readonly PostLayer layer = new();

    // Counts the updates for whoever works out how fast things are going, skipping one whenever there was a gap:
    // what is somewhere else after a break hasn't moved there
    private int motionFrame;
    private bool layerWarmed;

    // Whether what is on the layer is what the UI looks like right now, and what it was drawn with. Seen
    // through another camera the same list is another picture
    private bool pictureCurrent;
    private Matrix4x4 pictureView, pictureProjection;

    /// <summary>
    /// Whether the UI keeps the picture it last drew and lays that over the frame for as long as nothing in it
    /// changes, rather than drawing every quad of it again every frame. On unless it is switched off. A UI that
    /// is standing still (most of them, most of the time) then costs one quad a frame however much is in it, and
    /// nothing is sent to the GPU for it at all. What it takes is the memory of a picture the size of the screen.
    /// </summary>
    public bool Retained { get; set; } = true;

    /// <summary>
    /// The effects the UI goes through before it is laid over whatever is under it, none to begin with:
    /// <code>
    /// compositor.PostProcessing.Add(new MotionBlurEffect());
    /// </code>
    /// They only ever see the UI and leave what is behind it alone. While any of them is on the UI is drawn onto
    /// a layer of its own first (see <see cref="PostLayer"/>) and every component keeps track of how fast it is
    /// going across the screen, by where it ends up being drawn. Layout, tweens and the place of its module all
    /// count. With none on there is no layer and no keeping track, the UI costs what it does without any of this,
    /// and an effect that is only for what moves is skipped (layer and all) whenever nothing in the UI does.
    /// What a component paints inside of its own bounds (the handle of a slider) goes as fast as the component.
    /// </summary>
    public PostProcessor PostProcessing => layer.Effects;

    // Replaced wholesale on every change, so the logic thread can walk the array it has while
    // another thread creates a module.
    private readonly Lock modulesLock = new();
    private UIModule[] modules = [];

    // The logic thread paints into `back`, then swaps it in as `front` for the render thread to upload.
    private readonly Lock frameLock = new();
    private UIDrawList back = new();
    private UIDrawList front = new();
    private int paintedFrame;
    private int uploadedFrame;

    private bool pointerWasDown;
    private UIComponent? hovered;
    private UIComponent? pressed;
    private UIComponent? focus;

    public IReadOnlyList<UIModule> Modules => modules;

    /// <summary>The look of the UI, null until <see cref="Initialize"/> has loaded it (or if it failed to).</summary>
    public UISkin? Skin
    {
        get => skin;
        private set => skin = value;
    }

    // Swapped on the render thread, read by the updates, and only ever swapped for one that is all there
    private volatile UISkin? skin;

    /// <summary>
    /// A list as it was at the end of a tick, which frames that are drawn alongside the simulation are drawn from.
    /// </summary>
    private sealed class CapturedList
    {
        public SpriteItem[] Items = new SpriteItem[256];
        public readonly List<UIDrawList.Run> Runs = [];
        public int Count;
        public int Painted = -1;
        public UISkin? Skin;
        public bool Moving, Incomplete;

        public ReadOnlySpan<SpriteItem> Span => Items.AsSpan(0, Count);
    }

    private readonly SnapshotBuffer<CapturedList> captured = new(static () => new CapturedList());

    // What of the captured lists was uploaded last: which two lists and how far between them, -1 for something else.
    // And where blended lists are put together
    private int shownBefore = -1, shownAfter = -1;
    private float shownAlpha = float.NaN;
    private SpriteItem[] blended = [];

    /// <summary>
    /// Where the pointer comes from. The mouse unless set to something else, which could be a cursor
    /// moved with a gamepad, or a recording.
    /// </summary>
    public Func<UIPointer>? PointerSource { get; set; }

    /// <summary>
    /// Whether the UI has the pointer. It is over a component that stops it, or it is still held
    /// after being pressed on one. The game can use this to keep clicks on the UI to the UI.
    /// </summary>
    public bool IsPointerOverUI { get; private set; }

    /// <summary>
    /// The component that gets what is typed on the keyboard, null when nothing does. Pressing the
    /// pointer on a component that takes the focus sets it, pressing anywhere else clears it.
    /// </summary>
    public UIComponent? Focus
    {
        get => focus;
        set
        {
            if (focus == value)
                return;

            // Whatever was typed before now was not meant for this component.
            UIKeyboard.Clear();
            focus = value;
        }
    }

    /// <param name="viewportCamera">The camera the UI is drawn with. What it sees is the screen the modules are laid out against.</param>
    /// <param name="theme">Which theme of the usual skin to draw the UI in ("red", "gold"), null for the one the skin says is its usual one.</param>
    public UICompositor(in Camera2D viewportCamera, string? theme = null)
        : this(viewportCamera, DEFAULT_SKIN_DIRECTORY, DEFAULT_SKIN_FILE, theme) { }

    /// <param name="viewportCamera">The camera the UI is drawn with. What it sees is the screen the modules are laid out against.</param>
    /// <param name="skinDirectory">The directory holding the skin definition and its art.</param>
    /// <param name="skinFile">The name of the skin definition in that directory.</param>
    /// <param name="theme">Which of the skin's themes to use, null for the one it says is its usual one.</param>
    public UICompositor(in Camera2D viewportCamera, string skinDirectory, string skinFile, string? theme = null)
    {
        this.viewportCamera = viewportCamera;
        this.skinDirectory = skinDirectory;
        this.skinFile = skinFile;
        this.skinTheme = theme;
    }

    public UIModule CreateModule()
    {
        var module = new UIModule(this);

        lock (modulesLock)
            modules = [.. modules, module];

        return module;
    }

    public bool RemoveModule(UIModule module)
    {
        lock (modulesLock)
        {
            int index = Array.IndexOf(modules, module);
            if (index < 0)
                return false;

            modules = [.. modules[..index], .. modules[(index + 1)..]];
            return true;
        }
    }

    public override void Initialize()
    {
        // The one every UI with this skin draws with, see UISkin.Shared
        Skin = UISkin.Shared(skinDirectory, skinFile, skinTheme);

        renderer.Initialize();

        UIKeyboard.Hook();
    }

    /// <summary>
    /// Swaps the look of the whole UI for another skin, such as another theme of the same art. Safe
    /// from any thread: the skin is loaded the next time the UI is drawn, and if it can't be the
    /// current one stays.
    /// </summary>
    /// <param name="directory">The directory holding the skin definition and its art.</param>
    /// <param name="file">The name of the skin definition in that directory.</param>
    /// <param name="theme">Which of the skin's themes to use, null for the one it says is its usual one.</param>
    public void SetSkin(string directory, string file, string? theme = null)
    {
        lock (skinLock)
            requestedSkin = (directory, file, theme);
    }

    /// <summary>
    /// Swaps the UI over to another theme of the skin it has, see <see cref="UISkin.Themes"/> for the ones
    /// there are. Safe from any thread, like <see cref="SetSkin"/>.
    /// </summary>
    public void SetTheme(string? theme) => SetSkin(skinDirectory, skinFile, theme);

    private void LoadRequestedSkin()
    {
        (string Directory, string File, string? Theme)? request;
        lock (skinLock)
        {
            request = requestedSkin;
            requestedSkin = null;
        }

        if (request is not { } wanted || UISkin.Shared(wanted.Directory, wanted.File, wanted.Theme) is not { } skin)
            return;

        (skinDirectory, skinFile, skinTheme) = wanted;

        // What was painted so far shows art of the old skin's atlas, not of this one's
        Skin = skin;
        renderer.Clear();
        pictureCurrent = false;
    }

    public override void UpdateState(float dt)
    {
        if (Skin is not { } skin)
            return;

        long now = Environment.TickCount64;
        motionFrame += now - lastUpdate > MOTION_BREAK ? 2 : 1;
        lastUpdate = now;

        var snapshot = modules;

        // What the camera sees. Its bounds start at the bottom left corner, the world being Y-up.
        var bounds = viewportCamera.Bounds;
        var screen = new UIRect(
            new Vector2(bounds.X, bounds.Y),
            new Vector2(bounds.X + bounds.Width, bounds.Y + bounds.Height));

        // Worked out before anything else uses it. The pointer has to be scaled the same way the layout is.
        float fit = DesignSize is { X: > 0.0f, Y: > 0.0f } design && !screen.IsEmpty
            ? MathF.Min(screen.Width / design.X, screen.Height / design.Y)
            : 1.0f;
        UIScale = MathF.Max(0.01f, fit * Scale);

        // The pointer is tested against the layout of the previous update, which is the one on screen.
        RoutePointer(snapshot);
        RouteKeyboard();

        back.Begin(skin, motionFrame, dt, layer.Effects.IsActive);
        foreach (var module in snapshot)
        {
            if (!module.Enabled)
                continue;

            module.Update(dt);
            module.Layout(screen, skin);
            module.Paint(back);

            // On top of the module and with its transform, so the layout lines up with what it is the layout of.
            if (module.ShowInLayoutDebugger && LayoutOverlay is { } overlay)
                UILayoutOverlay.Paint(back, module, overlay, module.ToLocal(lastPointer));

            // Whatever an editor has picked out is marked whether the debugger is open or not.
            if (Highlighted is { } highlighted && highlighted.Module == module)
                UILayoutOverlay.PaintHighlight(back, highlighted);
        }
        back.End();

        // Nothing happened. What was painted is what is on screen already, and nobody needs to hear about it
        if (paintedFrame > 0 && back.SameAs(front))
            return;

        lock (frameLock)
        {
            (front, back) = (back, front);
            paintedFrame++;
        }
    }

    /// <summary>
    /// Publishes the list that was painted last, for the frames that are drawn alongside the simulation. Simulation
    /// thread, at the end of every tick. A UI that is updated and drawn by hand has to call this by hand as well (at the
    /// end of the tick, from the <c>Capture</c> of whatever owns it), or it is drawn from its newest list as it always was.
    /// </summary>
    public override void Capture()
    {
        if (captured.BeginPublish() is not { } into)
            return;

        lock (frameLock)
        {
            // Every slot is written to every tick, but a UI that stands still has nothing new to copy
            if (into.Painted == paintedFrame && into.Skin == front.Skin)
                return;

            ReadOnlySpan<SpriteItem> items = front.Items;
            if (into.Items.Length < items.Length)
                into.Items = new SpriteItem[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)items.Length)];

            items.CopyTo(into.Items);
            into.Count = items.Length;

            into.Runs.Clear();
            foreach (var run in front.Runs)
                into.Runs.Add(run);

            into.Painted = paintedFrame;
            into.Skin = front.Skin;
            into.Moving = front.Moving;
            into.Incomplete = front.Incomplete;
        }
    }

    /// <summary>
    /// Helper method to upload what a frame that is drawn alongside the simulation shows: the last two captured lists,
    /// blended to the moment between them. Only when that is something else than what was uploaded last.
    /// </summary>
    /// <returns>False if nothing was captured yet, the UI is drawn from its newest list then.</returns>
    private bool UploadCaptured(in RenderFrame frame, UISkin skin)
    {
        if (!captured.TryGet(frame, out CapturedList before, out CapturedList after, out bool continuous))
            return false;

        // Not to be shown until it is the UI as it is meant to look
        if (after.Painted == 0 || after.Incomplete || after.Skin != skin)
        {
            GameEngine.Instance.SceneManager.ReportUnfinished();
            return true;
        }

        bool sameList = before.Painted == after.Painted;
        bool canBlend = continuous && !sameList && before.Skin == skin && !before.Incomplete && Blendable(before, after);

        // A list that changed in steps is shown as it was until the moment is all the way at the newer one
        CapturedList shown = sameList || canBlend || frame.Alpha >= 1.0f || before.Skin != skin || before.Incomplete ? after : before;
        float alpha = canBlend ? frame.Alpha : 1.0f;

        int from = canBlend ? before.Painted : shown.Painted;
        if (from == shownBefore && shown.Painted == shownAfter && alpha == shownAlpha)
            return true;

        if (canBlend && alpha < 1.0f)
        {
            if (blended.Length < after.Count)
                blended = new SpriteItem[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)after.Count)];

            ReadOnlySpan<SpriteItem> a = before.Span, b = after.Span;
            for (int i = 0; i < b.Length; i++)
            {
                // The same quad if it shows the same thing the same way: then only where it is, how big and what colour moved
                blended[i] = a[i].Flags == b[i].Flags && a[i].TexMin == b[i].TexMin && a[i].TexMax == b[i].TexMax
                    ? SpriteItem.Blend(a[i], b[i], alpha)
                    : a[i];
            }

            renderer.Upload(blended.AsSpan(0, b.Length), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(after.Runs), skin, before.Moving || after.Moving);
        }
        else
        {
            renderer.Upload(shown.Span, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(shown.Runs), skin, shown.Moving);
        }

        (shownBefore, shownAfter, shownAlpha) = (from, shown.Painted, alpha);
        pictureCurrent = false;
        return true;
    }

    /// <summary>
    /// Helper method to say whether two lists are the same quads (the same many, in the same runs) so that they can be
    /// blended one quad with the other.
    /// </summary>
    private static bool Blendable(CapturedList before, CapturedList after) =>
        before.Count == after.Count &&
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(before.Runs).SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(after.Runs));

    /* For whoever is working on a layout rather than using it, an editor say */

    /// <summary>
    /// What of its layout the UI draws over itself (the edges of its components, their padding, the gaps between
    /// them), null for none of it, which is what it is unless somebody sets it. Only the modules that have
    /// <see cref="UIModule.ShowInLayoutDebugger"/> on are drawn over. Works the same in every build.
    /// </summary>
    public UILayoutOverlayOptions? LayoutOverlay { get; set; }

    /// <summary>
    /// A component of this UI that is marked out from the rest, null for none. What an editor has selected.
    /// </summary>
    public UIComponent? Highlighted { get; set; }

    public override void Render(float dt)
    {
        // What a text box copied goes to the clipboard here, on the thread that may touch it
        UIKeyboard.FlushClipboard();

        LoadRequestedSkin();

        if (Skin is not { } skin)
            return;

        RenderFrame frame = RenderFrame.Active;
        if (frame.IsDecoupled)
        {
            // Art that was asked for while painting is stitched into the atlas now, whichever list is shown
            skin.Update();

            if (UploadCaptured(frame, skin))
            {
                DrawUploaded(dt);
                return;
            }
        }

        lock (frameLock)
        {
            // Art that was asked for while painting is stitched into the atlas now. A frame that was painted
            // while any was missing is skipped, what was shown before it stays up until the next one has it.
            skin.Update();

            if (uploadedFrame != paintedFrame || shownAfter >= 0)
            {
                // A frame painted with a skin that has been swapped out since is skipped too.
                if (front.Skin == skin && !front.Incomplete)
                {
                    renderer.Upload(front);
                    pictureCurrent = false;
                }

                uploadedFrame = paintedFrame;

                // What was uploaded is the newest list, not one that was captured
                shownBefore = shownAfter = -1;
                shownAlpha = float.NaN;
            }

            // Nothing painted yet, or what was painted last is still waiting for its art. What is about to be
            // drawn is not the UI as it is meant to look. A scene that was just set isn't shown like that
            if (paintedFrame == 0 || front.Incomplete)
                GameEngine.Instance.SceneManager.ReportUnfinished();
        }

        DrawUploaded(dt);
    }

    /// <summary>
    /// Helper method to draw what was uploaded last, or lay the picture of it over the frame once more if it is still
    /// what the UI looks like.
    /// </summary>
    private void DrawUploaded(float dt)
    {
        // Onto its own layer and through its effects if it has any that are on, straight over the scene if not.
        // A UI that is standing still has nothing for a blur to do, and goes straight there as well
        // The very first frame goes through the layer whether anything moves or not. Making the layer and what its
        // effects need takes a moment, which is better spent while the UI is loading than on the first frame
        // something in it moves
        // Seen through a camera that has changed, what was drawn before is not what the UI looks like any more
        if (viewportCamera.View != pictureView || viewportCamera.Projection != pictureProjection)
            pictureCurrent = false;

        // Nothing has changed since the picture on the layer was drawn. That is laid over the frame as it is,
        // and none of the UI is drawn again
        if (Retained && pictureCurrent && layer.Replay(dt, renderer.Moving))
            return;

        bool layered = layer.Begin(renderer.Moving || !layerWarmed, Retained);
        layerWarmed = true;
        renderer.Draw(viewportCamera);

        if (layered)
            layer.End(dt);

        // Only a picture that made it onto the layer is there to be shown again
        pictureCurrent = layered;
        (pictureView, pictureProjection) = (viewportCamera.View, viewportCamera.Projection);
    }

    /// <summary>
    /// Frees what the UI has on the GPU: the layer it is drawn onto for its effects, along with the effects.
    /// GL thread. Its skin is not its own (see <see cref="UISkin.Shared"/>) and stays.
    /// </summary>
    public void Dispose()
    {
        layer.Dispose();

        renderer.Clear();
        Skin = null;

        GC.SuppressFinalize(this);
    }
}
