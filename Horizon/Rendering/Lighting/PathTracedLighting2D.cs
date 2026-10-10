using System.Diagnostics;
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
/// from every direction that didn't come straight from a lamp (gi_resolve.slang). The lamps themselves light
/// everything through the deferred pass, shadows and all. Walls throw back what falls on them, including what the
/// tracer found last frame, which is how light bounces more than once, and what glows is its own light.
/// </para>
/// <para>
/// Every frame's light is laid over what the frames before it found (<see cref="Accumulation"/>), moved along with
/// the camera, and the rays are turned a little every frame (<see cref="Jitter"/>), so over a few frames every
/// direction gets looked in and not the same few every time. Without that a light a few pixels across is hit by
/// some rays and missed by the ones next to them, which is spokes round a small lamp when the camera stands still
/// and spokes that change places every frame when it creeps along, the whole room shimmering.
/// </para>
/// All of it on the render thread, the settings from any thread.
/// </summary>
public sealed class PathTracedLighting2D : IDisposable
{
    private const uint UNIT_ALBEDO = 0, UNIT_MATERIAL = 1, UNIT_PREVIOUS = 2, UNIT_UPPER = 4, UNIT_RADIANCE = 6;

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

    /// <summary>
    /// The most cascades there are, however big the picture, which is what the lighting costs. Every one is as dear
    /// as the last, so four instead of six is a third off. The furthest of them always looks as far as the far
    /// corner of the picture, fewer of them loses no light, what it loses is how finely light from far off is
    /// looked for (the far cascade has fewer rays for its distance than the one above it would have had), and
    /// <see cref="Jitter"/> makes most of that back over a few frames. It can be changed while the game runs,
    /// from an options screen say, the cascades are made again on the next frame.
    /// </summary>
    public int MaxCascades { get; set; } = 6;

    /// <summary>How much of the light that falls on a wall it throws back, 0 to 1. Over 1 and it feeds on itself.</summary>
    public float Bounce { get; set; } = 0.8f;

    /// <summary>How much of what the tracer found goes into the picture.</summary>
    public float Strength { get; set; } = 1.0f;

    /// <summary>How much of the renderer's ambient is still there with the tracer on, which fills the dark in by itself.</summary>
    public float AmbientScale { get; set; } = 0.5f;

    /// <summary>
    /// How long (in seconds) the light takes to settle, which is what every frame is laid over the ones before it
    /// for, 0 for every frame on its own. A tenth of a second is long enough to be rid of the shimmer and short
    /// enough that a light coming on, or a spark going past, is followed as it happens. It is a time and not a
    /// number of frames, the light settles as quickly at sixty frames a second as at three thousand, only the
    /// more frames there are the more of them it is made of. The frames before are moved along with the camera,
    /// what moves by itself leaves a short tail of its light behind it, which in a dark room looks like light does.
    /// </summary>
    public float Accumulation { get; set; } = 0.1f;

    /// <summary>
    /// Whether the rays are turned a little every frame while the light is accumulated, see <see cref="Accumulation"/>.
    /// On, the light round a small lamp is round. Off, the rays go the same ways every frame, which is steadier
    /// frame by frame and shows the spokes between them.
    /// </summary>
    public bool Jitter { get; set; } = true;

    private static readonly TextureDefinition CascadeTexture = new(PixelFormat.Rgba16F, Smooth: true, Usage: TextureUsage.Sampled | TextureUsage.Storage);

    private readonly List<Texture> cascades = [];
    private Texture? resultA, resultB, radiance;
    private bool writeB;
    private uint width, height, cascadeWidth, cascadeHeight;
    private int cascadeCount;
    private Vector2? originBefore;

    // What the cascades were made for, see Fit. Changing any of it makes them again
    private int fittedCascades, fittedSpacing;
    private float fittedInterval;

    // How many frames there have been, which is how far the rays are turned (see Turn), and when the last one was.
    // A frame that comes long after the last (the lighting was off for a while) starts again from nothing
    private uint frames;
    private long lastRun;

    // How long after the last frame one still carries on from it, in seconds
    private const double GAP = 0.25;

    private Technique? radiancePass, cascadePass, resolvePass;

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

        radiancePass ??= new Technique(Shader.Load("shaders/lighting", "gi_radiance"));
        cascadePass ??= new Technique(Shader.Load("shaders/lighting", "gi_cascade"));
        resolvePass ??= new Technique(Shader.Load("shaders/lighting", "gi_resolve"));
        if (!radiancePass.IsValid || !cascadePass.IsValid || !resolvePass.IsValid) return;

        float scale = Math.Clamp(Scale, 0.1f, 1.0f);
        uint wanted = (uint)MathF.Max(1.0f, MathF.Ceiling(renderer.ViewportSize.X * scale));
        uint wantedHeight = (uint)MathF.Max(1.0f, MathF.Ceiling(renderer.ViewportSize.Y * scale));
        Fit(wanted, wantedHeight);
        if (resultA is null || resultB is null || radiance is null || cascades.Count == 0) return;

        Texture previous = writeB ? resultA : resultB;
        Texture result = writeB ? resultB : resultA;

        // The world is this many units across a pixel of the picture
        float pixelWorld = camera.Bounds.Width / width;

        // Whether last frame's light is worth anything, it is not the first time, the camera didn't cut, and the
        // lighting wasn't off for a while in between. When it is not, it is read from nowhere, the bounce of the
        // radiance pass and the history of the resolve both find nothing outside of the picture
        long now = Stopwatch.GetTimestamp();
        Vector2? moved = MeasureShift(camera);
        bool carriesOn = moved is not null && lastRun != 0 && (now - lastRun) / (double)Stopwatch.Frequency < GAP;
        Vector2 shift = carriesOn ? moved!.Value : new Vector2(2.0f);
        lastRun = now;

        // How much of this frame goes into the light, the rest being what the frames before found
        float blend = carriesOn ? Blend(dt, Accumulation) : 1.0f;
        float turn = blend < 1.0f && Jitter ? Turn(frames, Describe0Directions()) : 0.0f;
        frames++;

        // The furthest cascade looks as far as the far corner, however few of them there are, see MaxCascades
        float diagonal = MathF.Sqrt((float)width * width + (float)height * height);

        // Where the top left of the picture is in the world, which is what the probes are laid out from, see Offset
        Vector2 topLeft = TopLeftOf(camera);

        // What the walls throw back, once, for every ray that lands on one to read
        radiancePass.Bind();
        renderer.FrameBuffer.BindAttachment(AttachmentPoint.Color0, UNIT_ALBEDO);
        renderer.FrameBuffer.BindAttachment(AttachmentPoint.Color2, UNIT_MATERIAL);
        previous.Bind(UNIT_PREVIOUS);
        renderer.BindLighting(radiancePass);
        radiancePass.SetUniform("uSize", Size);
        radiancePass.SetUniform("uPixelWorld", pixelWorld);
        radiancePass.SetUniform("uBounce", Math.Clamp(Bounce, 0.0f, 1.5f));
        radiancePass.SetUniform("uShift", in shift);
        device.BindStorageImage(0, radiance);
        device.Dispatch((width + 7) / 8, (height + 7) / 8);
        device.Barrier(BarrierTargets.ShaderImages);

        // The cascades, from the furthest in, each merged into the one above it as it is made
        cascadePass.Bind();
        radiance.Bind(UNIT_RADIANCE);
        renderer.BindLighting(cascadePass);

        cascadePass.SetUniform("uSize", Size);
        cascadePass.SetUniform("uCascadeSize", new Vector2(cascadeWidth, cascadeHeight));
        cascadePass.SetUniform("uPixelWorld", pixelWorld);
        cascadePass.SetUniform("uRotation", turn);

        for (int i = cascadeCount - 1; i >= 0; i--)
        {
            Describe(i, out Vector2 probes, out float spacing, out float directions, out float start, out float end);
            bool hasUpper = i + 1 < cascadeCount;
            Describe(hasUpper ? i + 1 : i, out Vector2 upperProbes, out float upperSpacing, out float upperDirections, out _, out _);
            Vector2 offset = Offset(topLeft, pixelWorld, spacing);
            Vector2 upperOffset = Offset(topLeft, pixelWorld, upperSpacing);
            if (!hasUpper) end = MathF.Max(end, diagonal);

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
        previous.Bind(1);
        resolvePass.SetUniform("uShift", in shift);
        resolvePass.SetUniform("uBlend", blend);
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

    /// <summary>
    /// How much of a frame goes into the light when it is laid over the ones before, for a frame of so many
    /// seconds and a light that settles in so many (see <see cref="Accumulation"/>). Taken as a decay over time,
    /// so two frames of half the time leave as much of the old light as one of the whole, whatever the frame rate.
    /// Never all of the old light, a frame that took no time at all still counts for a bit.
    /// </summary>
    internal static float Blend(float dt, float settle)
    {
        if (settle <= 0.0f) return 1.0f;
        return Math.Clamp(1.0f - MathF.Exp(-MathF.Max(dt, 0.0f) / settle), 0.02f, 1.0f);
    }

    /// <summary>
    /// How far the rays are turned on a frame, in radians, somewhere within the angle between two rays of the
    /// nearest cascade. Every cascade is turned by the same angle, which keeps the four rays a ray splits into
    /// above it inside of its cone, the merges never notice. The angles go round by the golden ratio, so any few
    /// frames in a row are spread out over the whole angle and none of them are near each other, rather than
    /// whatever a random number felt like. Turned by a fraction of a cone a ray is the same ray as far as the
    /// nearest cascade goes, and every cascade further out sees that fraction of a cone of its own, each of them
    /// is swept over a few frames.
    /// </summary>
    internal static float Turn(uint frame, float directions)
    {
        const double GOLDEN = 0.61803398874989485;
        double along = frame * GOLDEN % 1.0;
        return (float)(along * Math.Tau / Math.Max(directions, 1.0f));
    }

    /// <summary>Helper method for how many rays a probe of the nearest cascade has.</summary>
    private float Describe0Directions()
    {
        Describe(0, out _, out _, out float directions, out _, out _);
        return directions;
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
        // What the cascades were made for, not what the settings say this moment. Those can be changed from
        // another thread halfway through a frame, and a cascade described by one and made by the other is a
        // texture read past its end
        spacing = fittedSpacing * (1 << cascade);

        // Four to the cascade, which is two shifted and no business of Pow's. This is asked a dozen times a frame
        float four = MathF.ScaleB(1.0f, 2 * cascade);
        directions = 4.0f * four;

        // One more each way than the picture takes, the grid is pinned to the world and may start up to a spacing
        // before the picture does, see Offset
        probes = new Vector2(MathF.Ceiling(width / spacing) + 1.0f, MathF.Ceiling(height / spacing) + 1.0f);

        start = fittedInterval * (four - 1.0f) / 3.0f;
        end = fittedInterval * (four * 4.0f - 1.0f) / 3.0f;
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
        int most = Math.Max(1, MaxCascades), spacing = Math.Max(1, ProbeSpacing);
        float interval = MathF.Max(BaseInterval, 1.0f);

        if (resultA is not null && width == wantedWidth && height == wantedHeight
            && fittedCascades == most && fittedSpacing == spacing && fittedInterval == interval) return;

        Release();
        width = wantedWidth;
        height = wantedHeight;
        (fittedCascades, fittedSpacing, fittedInterval) = (most, spacing, interval);

        // How many cascades it takes to reach across the picture, and how big their textures have to be. A cascade
        // texture is probes times rays each way, which can run a little past the picture
        float diagonal = MathF.Sqrt(width * width + height * height);
        cascadeCount = 0;
        cascadeWidth = cascadeHeight = 0;
        for (int i = 0; i < most; i++)
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
            made.Asset.Name = $"cascade {i}";
            cascades.Add(made.Asset);
        }

        // Kept as they are made. A card that ran out of room halfway used to leave the ones it did make with
        // nobody holding them, and then try the whole lot again the next frame, and the next, god help it
        resultA = Picture("path traced result a");
        resultB = Picture("path traced result b");
        radiance = Picture("wall radiance");

        Texture? Picture(string name)
        {
            if (!objects.Textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = CascadeTexture }, out var made)) return null;
            made.Asset.Name = name;
            return made.Asset;
        }

        originBefore = null;
        lastRun = 0;
        Result = null;
    }

    private void Release()
    {
        foreach (Texture cascade in cascades) cascade.Dispose();
        cascades.Clear();
        resultA?.Dispose();
        resultB?.Dispose();
        radiance?.Dispose();
        resultA = resultB = radiance = null;
        Result = null;
    }

    public void Dispose() => Release();
}
