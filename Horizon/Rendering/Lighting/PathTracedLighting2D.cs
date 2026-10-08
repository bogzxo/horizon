using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Lighting;

/// <summary>How a <see cref="DeferredRenderer2D"/> lights what is drawn into it.</summary>
public enum LightingMode
{
    /// <summary>
    /// Every pixel is lit by every light that reaches it, with shadows traced to each. Quick, crisp, no light
    /// goes round a corner.
    /// </summary>
    Direct,

    /// <summary>
    /// The same, and on top of it the light that bounces, as found by <see cref="PathTracedLighting2D"/>. Rays go out
    /// from everywhere and gather what the walls they hit throw back, so light goes round corners, fills rooms from a
    /// torch on the wall and spills out of doorways. The fancy one.
    /// </summary>
    PathTraced
}

/// <summary>
/// Global illumination for a flat world, done as radiance cascades in compute. The settings of
/// <see cref="DeferredRenderer2D.PathTracing"/>, and the passes that do the work when
/// <see cref="DeferredRenderer2D.Lighting"/> is <see cref="LightingMode.PathTraced"/>.
/// <para>
/// The picture the lighting is worked out at (<see cref="Scale"/> of the renderer) is covered by a few cascades of
/// probes. The nearest has a probe every couple of pixels with four rays each over the first few pixels, every
/// cascade up has a quarter of the probes, four times the rays and an interval four times as long, so each costs the
/// same and five or six of them reach across the whole picture. Every ray marches the distance field of the walls and
/// stops at the first one, and a cascade is merged with the one above it as it is made
/// (shaders/lighting/gi_cascade.slang). What the nearest cascade ends up with is the light arriving at every pixel
/// from every direction that didn't come straight from a lamp (gi_resolve.slang), with no noise in it, nothing to
/// smooth over frames and nothing to reproject. The lamps themselves light everything through the deferred pass,
/// shadows and all. Walls throw back what falls on them, including what the tracer found last frame, which is how
/// light bounces more than once, and what glows is its own light.
/// </para>
/// All of it on the render thread, the settings from any thread.
/// </summary>
public sealed class PathTracedLighting2D : IDisposable
{
    private const uint UNIT_ALBEDO = 0, UNIT_SURFACE = 1, UNIT_PREVIOUS = 2, UNIT_UPPER = 4;

    /// <summary>
    /// How big the lighting is worked out at, as a share of the renderer's size. Half, which is plenty for light that
    /// bounces, soft by nature, and a quarter of the rays. The lamps themselves and their shadows are worked out by
    /// the deferred pass at full size whatever this is.
    /// </summary>
    public float Scale { get; set; } = 0.5f;

    /// <summary>How many pixels (of the picture) apart the probes of the nearest cascade are. 2 is the usual, 1 is sharper and four times the work.</summary>
    public int ProbeSpacing { get; set; } = 2;

    /// <summary>How far (in pixels of the picture) the rays of the nearest cascade reach. Each cascade up reaches four times further.</summary>
    public float BaseInterval { get; set; } = 4.0f;

    /// <summary>The most cascades there are, however big the picture. Each one is as dear as the last.</summary>
    public int MaxCascades { get; set; } = 6;

    /// <summary>How much of the light that falls on a wall it throws back, 0 to 1. Over 1 and it feeds on itself.</summary>
    public float Bounce { get; set; } = 0.8f;

    /// <summary>How much of what the tracer found goes into the picture.</summary>
    public float Strength { get; set; } = 1.0f;

    /// <summary>How much of the renderer's ambient is still there with the tracer on, which fills the dark in by itself.</summary>
    public float AmbientScale { get; set; } = 0.5f;

    private static readonly TextureDefinition CascadeTexture = new(PixelFormat.Rgba16F, Smooth: true, Usage: TextureUsage.Sampled | TextureUsage.Storage);

    private readonly List<Texture> cascades = [];
    private Texture? resultA, resultB;
    private bool writeB;
    private uint width, height, cascadeWidth, cascadeHeight;
    private int cascadeCount;
    private Vector2? originBefore;

    private Technique? cascadePass, resolvePass;

    /// <summary>What the tracer found last, for the deferred pass. Null before the first frame.</summary>
    public Texture? Result { get; private set; }

    /// <summary>The size of the picture everything is worked out at.</summary>
    public Vector2 Size => new(width, height);

    /// <summary>How many cascades the last frame had.</summary>
    public int CascadeCount => cascadeCount;

    /// <summary>
    /// Runs the passes for a frame. Render thread, with the G-buffer drawn, the lights uploaded and sorted into tiles,
    /// and the camera block set to the camera.
    /// </summary>
    internal void Run(DeferredRenderer2D renderer, Camera camera, float dt)
    {
        var device = GraphicsDevice.Current;

        cascadePass ??= new Technique(Shader.Load("shaders/lighting", "gi_cascade"));
        resolvePass ??= new Technique(Shader.Load("shaders/lighting", "gi_resolve"));
        if (!cascadePass.IsValid || !resolvePass.IsValid) return;

        float scale = Math.Clamp(Scale, 0.1f, 1.0f);
        uint wanted = (uint)MathF.Max(1.0f, MathF.Ceiling(renderer.ViewportSize.X * scale));
        uint wantedHeight = (uint)MathF.Max(1.0f, MathF.Ceiling(renderer.ViewportSize.Y * scale));
        Fit(wanted, wantedHeight);
        if (resultA is null || resultB is null || cascades.Count == 0) return;

        Texture previous = writeB ? resultA : resultB;
        Texture result = writeB ? resultB : resultA;

        // The world is this many units across a pixel of the picture
        float pixelWorld = camera.Bounds.Width / width;
        Vector2 shift = MeasureShift(camera) ?? Vector2.Zero;

        // Where the top left of the picture is in the world, which is what the probes are laid out from, see Offset
        Vector2 topLeft = TopLeftOf(camera);

        // The cascades, from the furthest in, each merged into the one above it as it is made
        cascadePass.Bind();
        renderer.FrameBuffer.BindAttachment(AttachmentPoint.Color0, UNIT_ALBEDO);
        renderer.FrameBuffer.BindAttachment(AttachmentPoint.Color1, UNIT_SURFACE);
        previous.Bind(UNIT_PREVIOUS);
        renderer.BindLighting(cascadePass);

        cascadePass.SetUniform("uSize", Size);
        cascadePass.SetUniform("uCascadeSize", new Vector2(cascadeWidth, cascadeHeight));
        cascadePass.SetUniform("uPixelWorld", pixelWorld);
        cascadePass.SetUniform("uBounce", Math.Clamp(Bounce, 0.0f, 1.5f));
        cascadePass.SetUniform("uShift", in shift);

        for (int i = cascadeCount - 1; i >= 0; i--)
        {
            Describe(i, out Vector2 probes, out float spacing, out float directions, out float start, out float end);
            bool hasUpper = i + 1 < cascadeCount;
            Describe(hasUpper ? i + 1 : i, out Vector2 upperProbes, out float upperSpacing, out float upperDirections, out _, out _);
            Vector2 offset = Offset(topLeft, pixelWorld, spacing);
            Vector2 upperOffset = Offset(topLeft, pixelWorld, upperSpacing);

            cascadePass.SetUniform("uProbeCount", in probes);
            cascadePass.SetUniform("uProbeSpacing", spacing);
            cascadePass.SetUniform("uProbeOffset", in offset);
            cascadePass.SetUniform("uDirections", directions);
            cascadePass.SetUniform("uIntervalStart", start);
            cascadePass.SetUniform("uIntervalEnd", end);
            cascadePass.SetUniform("uUpperProbeCount", in upperProbes);
            cascadePass.SetUniform("uUpperSpacing", upperSpacing);
            cascadePass.SetUniform("uUpperOffset", in upperOffset);
            cascadePass.SetUniform("uUpperDirections", upperDirections);
            cascadePass.SetUniform("uHasUpper", hasUpper);

            // The top cascade has nothing above it, it is given something harmless to read
            (hasUpper ? cascades[i + 1] : previous).Bind(UNIT_UPPER);
            device.BindStorageImage(0, cascades[i]);
            device.Dispatch((cascadeWidth + 7) / 8, (cascadeHeight + 7) / 8);
            device.Barrier(BarrierTargets.ShaderImages);
        }

        // And what every pixel sees out of the nearest one
        Describe(0, out Vector2 nearestProbes, out float nearestSpacing, out float nearestDirections, out _, out _);
        Vector2 nearestOffset = Offset(topLeft, pixelWorld, nearestSpacing);

        resolvePass.Bind();
        cascades[0].Bind(0);
        resolvePass.SetUniform("uSize", Size);
        resolvePass.SetUniform("uProbeCount", in nearestProbes);
        resolvePass.SetUniform("uProbeSpacing", nearestSpacing);
        resolvePass.SetUniform("uProbeOffset", in nearestOffset);
        resolvePass.SetUniform("uDirections", nearestDirections);
        device.BindStorageImage(0, result);
        device.Dispatch((width + 7) / 8, (height + 7) / 8);
        device.Barrier(BarrierTargets.ShaderImages);
        resolvePass.Unbind();
        device.BindStorageImage(0, null);

        Result = result;
        writeB = !writeB;
    }

    /// <summary>
    /// Helper method for where the probes of a cascade sit against the picture, in pixels, which is up to the world
    /// and not the picture. A probe grid that rode along with the camera would have every probe see something a
    /// little different every time the camera moved a pixel, and a lantern a few pixels wide would be hit by a ray
    /// one frame and missed the next, which showed up as the lamps breathing while the camera panned. So the grid
    /// is pinned to the world instead, every cascade's probes sit on multiples of their spacing in world units,
    /// and the picture is offset to that, by up to a spacing. The grid of a cascade is a grid of the one above it
    /// as well, so the merges line up.
    /// </summary>
    private static Vector2 Offset(Vector2 topLeft, float pixelWorld, float spacing)
    {
        // Where the top left of the picture falls within a cell of the probe grid, in pixels of the picture
        float x = Mod(topLeft.X / pixelWorld, spacing);
        float y = Mod(topLeft.Y / pixelWorld, spacing);

        // Down the picture the world goes the other way, see the sums in Run
        return new Vector2(-x, y - spacing);

        static float Mod(float a, float b)
        {
            float m = a % b;
            return m < 0.0f ? m + b : m;
        }
    }

    /// <summary>Helper method for where the top left of the picture is in the world.</summary>
    private static Vector2 TopLeftOf(Camera camera)
    {
        if (!Matrix4x4.Invert(camera.ViewProj, out Matrix4x4 inverse)) return Vector2.Zero;

        // Clip space the way the engine has it before the correction for Vulkan, Y up, so the top left is (-1, 1)
        Vector4 corner = Vector4.Transform(new Vector4(-1.0f, 1.0f, 0.0f, 1.0f), inverse);
        return new Vector2(corner.X, corner.Y) / corner.W;
    }

    /// <summary>Helper method for what a cascade is, how many probes, how far apart, how many rays each, over which distances.</summary>
    private void Describe(int cascade, out Vector2 probes, out float spacing, out float directions, out float start, out float end)
    {
        int baseSpacing = Math.Max(1, ProbeSpacing);
        spacing = baseSpacing * (1 << cascade);
        directions = 4.0f * MathF.Pow(4.0f, cascade);

        // One more each way than the picture takes, the grid is pinned to the world and may start up to a spacing
        // before the picture does, see Offset
        probes = new Vector2(MathF.Ceiling(width / spacing) + 1.0f, MathF.Ceiling(height / spacing) + 1.0f);

        float interval = MathF.Max(BaseInterval, 1.0f);
        start = interval * (MathF.Pow(4.0f, cascade) - 1.0f) / 3.0f;
        end = interval * (MathF.Pow(4.0f, cascade + 1) - 1.0f) / 3.0f;
    }

    /// <summary>
    /// Helper method for how far the camera moved the picture since the last frame, in texture coordinates, so the
    /// last frame's light can be read from where it is now. Null the first time and after a cut.
    /// </summary>
    private Vector2? MeasureShift(Camera camera)
    {
        Vector4 projected = Vector4.Transform(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), camera.ViewProj);
        var origin = new Vector2(projected.X, -projected.Y);

        Vector2? before = originBefore;
        originBefore = origin;

        if (before is not { } was) return null;

        // The screen is two units across the way the GPU counts it, a picture is one
        Vector2 shift = (was - origin) * 0.5f;
        return MathF.Abs(shift.X) > 0.5f || MathF.Abs(shift.Y) > 0.5f ? null : shift;
    }

    /// <summary>Helper method to have every texture be the size that is wanted, made anew when it isn't.</summary>
    private void Fit(uint wantedWidth, uint wantedHeight)
    {
        if (resultA is not null && width == wantedWidth && height == wantedHeight) return;

        Release();
        width = wantedWidth;
        height = wantedHeight;

        // How many cascades it takes to reach across the picture, and how big their textures have to be. A cascade
        // texture is probes times rays each way, which can run a little past the picture
        float diagonal = MathF.Sqrt(width * width + height * height);
        cascadeCount = 0;
        cascadeWidth = cascadeHeight = 0;
        for (int i = 0; i < Math.Max(1, MaxCascades); i++)
        {
            Describe(i, out Vector2 probes, out _, out float directions, out float start, out _);
            if (i > 0 && start >= diagonal) break;

            uint sqrt = (uint)MathF.Round(MathF.Sqrt(directions));
            cascadeWidth = Math.Max(cascadeWidth, (uint)probes.X * sqrt);
            cascadeHeight = Math.Max(cascadeHeight, (uint)probes.Y * sqrt);
            cascadeCount++;
        }

        var objects = GameEngine.Instance.ObjectManager;
        for (int i = 0; i < cascadeCount; i++)
        {
            if (!objects.Textures.TryCreate(new TextureDescription { Width = cascadeWidth, Height = cascadeHeight, Definition = CascadeTexture }, out var made)) return;
            cascades.Add(made.Asset);
        }

        if (!objects.Textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = CascadeTexture }, out var a)) return;
        if (!objects.Textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = CascadeTexture }, out var b)) return;
        resultA = a.Asset;
        resultB = b.Asset;

        originBefore = null;
        Result = null;
    }

    private void Release()
    {
        foreach (Texture cascade in cascades) cascade.Dispose();
        cascades.Clear();
        resultA?.Dispose();
        resultB?.Dispose();
        resultA = resultB = null;
        Result = null;
    }

    public void Dispose() => Release();
}
