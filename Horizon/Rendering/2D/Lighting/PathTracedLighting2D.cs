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
/// and spokes that change places every frame when it creeps along, the whole room shimmering. Every pixel lets go
/// of what it had when the light there changes by more than it usually wavers (<see cref="Responsiveness"/>), so a
/// change is taken as it happens and the shimmer is smoothed all the same.
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
    /// number of frames, every pixel holds on to so many seconds' worth of light and a frame goes in by how long it
    /// was, so the light settles as quickly at sixty frames a second as at three thousand, and a slow frame (a
    /// hitch) counts for more just the once rather than knocking every pixel back for the frames after it. The
    /// frames before are moved along with the camera, what moves by itself leaves a short tail of its light behind
    /// it, which in a dark room looks like light does (and see <see cref="Responsiveness"/>).
    /// </summary>
    public float Accumulation { get; set; } = 0.1f;

    /// <summary>
    /// Whether the rays are turned a little every frame while the light is accumulated, see <see cref="Accumulation"/>.
    /// On, the light round a small lamp is round. Off, the rays go the same ways every frame, which is steadier
    /// frame by frame and shows the spokes between them.
    /// </summary>
    public bool Jitter { get; set; } = true;

    /// <summary>
    /// Whether the light is kept at the size of the renderer (times <see cref="OutputScale"/>) rather than at the
    /// size it is traced at (<see cref="Scale"/>), and filled in to every pixel over a few frames. The whole grid
    /// of probes is moved by a fraction of a probe every frame, a different fraction every time, and a pixel takes
    /// most from the frames that had a probe close to it, which was to give the light detail finer than the probes
    /// are apart and let Scale come down (the tracing costs the square of it) without the light going soft.
    /// <para>
    /// Off unless asked for, because measured it doesn't do that yet. On the dungeon example traced at a quarter
    /// size it looked a bit better than a quarter size without it and nowhere near half size, and it was less
    /// steady from frame to frame at any size. The detail is in the picture the rays read what glows from, which
    /// is still traced at Scale, and moving the probes about does nothing for that. Moving that picture about with
    /// them is the next step, see docs/radiance-cascades.md. It costs a pass over the light at its full size and
    /// the memory of four pictures of it at half floats.
    /// </para>
    /// </summary>
    public bool Upscaling { get; set; }

    /// <summary>How big the light is kept while upscaling, as a share of the renderer's size. 1 is every pixel, less saves memory and a little time.</summary>
    public float OutputScale { get; set; } = 1.0f;

    /// <summary>
    /// How readily a pixel lets go of the light it had when the light there changes, which is how the light keeps
    /// up with what moves. Every pixel knows how much its light usually wavers from frame to frame (the shimmer of
    /// the turning rays, the flicker of a creeping camera), and a change well past that is something that happened,
    /// a spark flying past, a brazier catching, and the pixel starts again from this frame rather than taking a
    /// tenth of a second to come round to it. 0 never lets go and every change is smoothed alike, 1 is the usual,
    /// 2 lets go at half the change. A camera moving needs none of this, the light is moved along with it anyway.
    /// </summary>
    public float Responsiveness { get; set; } = 1.0f;

    private static readonly TextureDefinition CascadeTexture = new(PixelFormat.Rgba16F, Smooth: true, Usage: TextureUsage.Sampled | TextureUsage.Storage);

    private readonly List<Texture> cascades = [];
    private Texture? resultA, resultB, radiance;

    // How much the light of every pixel usually wavers, the mean of its brightness and of its square, kept the way
    // the light is and taking turns with it. See Responsiveness
    private Texture? momentsA, momentsB;

    private bool writeB;
    private uint width, height, cascadeWidth, cascadeHeight;

    // The size the light is kept at, the traced picture's unless it is upscaled
    private uint outputWidth, outputHeight;
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

    /// <summary>The size of the picture everything is traced at.</summary>
    public Vector2 Size => new(width, height);

    /// <summary>The size the light is kept at, which is <see cref="Size"/> unless it is upscaled.</summary>
    public Vector2 OutputSize => new(outputWidth, outputHeight);

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

        // Read once, it can be changed from another thread halfway through
        bool upscaling = Upscaling;
        float output = upscaling ? Math.Clamp(OutputScale, scale, 1.0f) : scale;
        uint wantedOutput = upscaling ? (uint)MathF.Max(wanted, MathF.Ceiling(renderer.ViewportSize.X * output)) : wanted;
        uint wantedOutputHeight = upscaling ? (uint)MathF.Max(wantedHeight, MathF.Ceiling(renderer.ViewportSize.Y * output)) : wantedHeight;

        Fit(wanted, wantedHeight, wantedOutput, wantedOutputHeight);
        if (resultA is null || resultB is null || momentsA is null || momentsB is null || radiance is null || cascades.Count == 0) return;

        Texture previous = writeB ? resultA : resultB;
        Texture result = writeB ? resultB : resultA;
        Texture previousMoments = writeB ? momentsA : momentsB;
        Texture moments = writeB ? momentsB : momentsA;

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

        // Whether the frames before count at all, and for how long, see Accumulation
        float settle = carriesOn ? MathF.Max(Accumulation, 0.0f) : 0.0f;
        bool accumulating = settle > 0.0f;
        float turn = accumulating && Jitter ? Turn(frames, Describe0Directions()) : 0.0f;

        // Where the probes are moved to this frame, in world units, see Upscaling. Every cascade is moved by the
        // same distance, the lot moves as one and the merges never notice
        bool sharpen = upscaling && accumulating;
        Vector2 nudge = sharpen ? Nudge(frames) * (fittedSpacing * pixelWorld) : Vector2.Zero;
        frames++;

        // The furthest cascade looks as far as the far corner, however few of them there are, see MaxCascades
        float diagonal = MathF.Sqrt((float)width * width + (float)height * height);

        // Where the top left of the picture is in the world, which is what the probes are laid out from, see Offset.
        // Less the nudge, which moves every probe by it
        Vector2 topLeft = TopLeftOf(camera) - nudge;

        // What the walls throw back, once, for every ray that lands on one to read
        using (device.BeginGpuScope("wall radiance"))
        {
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
        }

        // The cascades, from the furthest in, each merged into the one above it as it is made
        using var tracing = device.BeginGpuScope("cascades");
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
            cascadePass.SetUniform("uDirectionsSqrt", MathF.Sqrt(directions));
            cascadePass.SetUniform("uIntervalStart", start);
            cascadePass.SetUniform("uIntervalEnd", end);
            cascadePass.SetUniform("uUpperProbeCount", in upperProbes);
            cascadePass.SetUniform("uUpperSpacing", upperSpacing);
            cascadePass.SetUniform("uUpperOffset", in upperOffset);
            cascadePass.SetUniform("uUpperDirectionsSqrt", MathF.Sqrt(upperDirections));
            cascadePass.SetUniform("uHasUpper", hasUpper);

            // The top cascade has nothing above it, it is given something harmless to read
            (hasUpper ? cascades[i + 1] : previous).Bind(UNIT_UPPER);
            device.BindStorageImage(0, cascades[i]);
            device.Dispatch((cascadeWidth + 7) / 8, (cascadeHeight + 7) / 8);
            device.Barrier(BarrierTargets.ShaderImages);
        }

        tracing.Dispose();

        // And what every pixel sees out of the nearest one
        Describe(0, out Vector2 nearestProbes, out float nearestSpacing, out float nearestDirections, out _, out _);
        Vector2 nearestOffset = Offset(topLeft, pixelWorld, nearestSpacing);

        using var resolving = device.BeginGpuScope("gi resolve");
        resolvePass.Bind();
        cascades[0].Bind(0);
        previous.Bind(1);
        previousMoments.Bind(2);
        resolvePass.SetUniform("uShift", in shift);

        // How many seconds' worth a pixel may hold on to, and how long this frame was, which is how much of the
        // frame goes in. A frame that took no time at all still counts for a bit
        resolvePass.SetUniform("uSettle", settle);
        resolvePass.SetUniform("uFrameTime", Math.Clamp(dt, 0.0001f, 0.25f));
        resolvePass.SetUniform("uSharpen", sharpen ? 1.0f : 0.0f);
        resolvePass.SetUniform("uResponsiveness", MathF.Max(Responsiveness, 0.0f));
        resolvePass.SetUniform("uSize", OutputSize);
        resolvePass.SetUniform("uTraceSize", Size);
        resolvePass.SetUniform("uProbeCount", in nearestProbes);
        resolvePass.SetUniform("uProbeSpacing", nearestSpacing);
        resolvePass.SetUniform("uProbeOffset", in nearestOffset);
        resolvePass.SetUniform("uDirections", nearestDirections);
        resolvePass.SetUniform("uDirectionsSqrt", MathF.Sqrt(nearestDirections));
        resolvePass.SetUniform("uCascadeSize", new Vector2(cascadeWidth, cascadeHeight));
        device.BindStorageImage(0, result);
        device.BindStorageImage(1, moments);
        device.Dispatch((outputWidth + 7) / 8, (outputHeight + 7) / 8);
        device.Barrier(BarrierTargets.ShaderImages);
        resolvePass.Unbind();
        device.BindStorageImage(0, null);
        device.BindStorageImage(1, null);

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

    /// <summary>
    /// Where in a cell of the probe grid the probes are moved to on a frame, from 0 to 1 each way. Halton's sequence
    /// in two and three, which fills the cell evenly for any few frames in a row (the first eight have been in
    /// every quarter of it), and goes round every hundred and twenty eight, too long to see.
    /// </summary>
    internal static Vector2 Nudge(uint frame)
    {
        uint index = frame % 128 + 1;
        return new Vector2(Halton(index, 2), Halton(index, 3));

        static float Halton(uint index, uint radix)
        {
            float result = 0.0f, fraction = 1.0f;
            while (index > 0)
            {
                fraction /= radix;
                result += fraction * (index % radix);
                index /= radix;
            }

            return result;
        }
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
    private void Fit(uint wantedWidth, uint wantedHeight, uint wantedOutput, uint wantedOutputHeight)
    {
        int most = Math.Max(1, MaxCascades), spacing = Math.Max(1, ProbeSpacing);
        float interval = MathF.Max(BaseInterval, 1.0f);

        if (resultA is not null && width == wantedWidth && height == wantedHeight
            && outputWidth == wantedOutput && outputHeight == wantedOutputHeight
            && fittedCascades == most && fittedSpacing == spacing && fittedInterval == interval) return;

        Release();
        width = wantedWidth;
        height = wantedHeight;
        outputWidth = wantedOutput;
        outputHeight = wantedOutputHeight;
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
        resultA = Picture("path traced result a", outputWidth, outputHeight);
        resultB = Picture("path traced result b", outputWidth, outputHeight);
        momentsA = Picture("path traced wavering a", outputWidth, outputHeight);
        momentsB = Picture("path traced wavering b", outputWidth, outputHeight);
        radiance = Picture("wall radiance", width, height);

        Texture? Picture(string name, uint across, uint down)
        {
            if (!objects.Textures.TryCreate(new TextureDescription { Width = across, Height = down, Definition = CascadeTexture }, out var made)) return null;
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
        momentsA?.Dispose();
        momentsB?.Dispose();
        radiance?.Dispose();
        resultA = resultB = momentsA = momentsB = radiance = null;
        Result = null;
    }

    public void Dispose() => Release();
}
