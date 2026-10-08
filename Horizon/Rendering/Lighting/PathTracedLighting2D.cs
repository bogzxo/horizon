using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.PostProcessing;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

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
    /// from every pixel and gather what the walls they hit throw back and what the lights give off as things with a
    /// size, frame after frame, so light goes round corners, fills rooms from a torch on the wall and spills out of
    /// doorways. The fancy one.
    /// </summary>
    PathTraced
}

/// <summary>
/// Global illumination for a flat world, done by path tracing the screen. The settings of
/// <see cref="DeferredRenderer2D.PathTracing"/>, and the passes that do the work when
/// <see cref="DeferredRenderer2D.Lighting"/> is <see cref="LightingMode.PathTraced"/>.
/// <para>
/// Every frame the scene is drawn small (<see cref="Scale"/> of the renderer) as what gives off light and what
/// stops it (shaders/lighting/gi_scene.frag), a jump flood works out for every texel how far the nearest wall is
/// (gi_flood, gi_distance), every texel sends out <see cref="Rays"/> rays that march through that field until they
/// hit something and gather its radiance (gi_trace), what they found is blended with the last frames' (moved along
/// with the camera, so a pan doesn't smear) and smoothed without crossing the edges of walls (gi_denoise). The
/// result goes into the deferred pass as the light that isn't straight from a light. Walls throw back what falls on
/// them including what the tracer found last frame, which is how the light bounces more than once over a few frames.
/// </para>
/// All of it on the render thread, the settings from any thread.
/// </summary>
public sealed class PathTracedLighting2D : IDisposable
{
    // The texture units the passes expect, see the shaders
    private const uint UNIT_ALBEDO = 0, UNIT_SURFACE = 1, UNIT_GI_PREVIOUS = 4;

    /// <summary>
    /// How big the lighting is worked out at, as a share of the renderer's size. Half is plenty for light, which is
    /// soft by nature, and a quarter of the rays.
    /// </summary>
    public float Scale { get; set; } = 0.5f;

    /// <summary>How many rays every texel sends out a frame. More is smoother sooner, and costs as much more.</summary>
    public int Rays { get; set; } = 24;

    /// <summary>The most steps a ray takes before it is given up on.</summary>
    public int Steps { get; set; } = 48;

    /// <summary>How far a ray reaches at the most, as a share of the width of the picture.</summary>
    public float Reach { get; set; } = 1.0f;

    /// <summary>How much of the light that falls on a wall it throws back, 0 to 1. Over 1 and it feeds on itself.</summary>
    public float Bounce { get; set; } = 0.8f;

    /// <summary>
    /// How much of the last frame's result is kept, 0 to 1. The higher the smoother and the longer light takes to
    /// settle when something changes. By the clock, at sixty frames a second.
    /// </summary>
    public float Smoothing { get; set; } = 0.9f;

    /// <summary>How big a light is as a thing for rays to hit, in world units, for a light without a <see cref="Light2D.Size"/>.</summary>
    public float EmitterRadius { get; set; } = 6.0f;

    /// <summary>How bright a light is as a thing for rays to hit, on top of its intensity. Small lights want more.</summary>
    public float EmitterBoost { get; set; } = 4.0f;

    /// <summary>How much of what the tracer found goes into the picture.</summary>
    public float Strength { get; set; } = 1.0f;

    /// <summary>How much of the renderer's ambient is still there with the tracer on, which fills the dark in by itself.</summary>
    public float AmbientScale { get; set; } = 0.5f;

    /// <summary>How far (in texels of the small picture) the result is smoothed, 0 for not at all.</summary>
    public float Blur { get; set; } = 1.5f;

    private static readonly TextureDefinition Seeds = new()
    {
        InternalFormat = InternalFormat.Rgba32f,
        PixelFormat = PixelFormat.Rgba,
        PixelType = PixelType.Float,
        TextureTarget = TextureTarget.Texture2D,
        Parameters =
        [
            new() { Name = TextureParameterName.TextureWrapS, Value = (int)GLEnum.ClampToEdge },
            new() { Name = TextureParameterName.TextureWrapT, Value = (int)GLEnum.ClampToEdge },
            new() { Name = TextureParameterName.TextureMinFilter, Value = (int)GLEnum.Nearest },
            new() { Name = TextureParameterName.TextureMagFilter, Value = (int)GLEnum.Nearest },
            new() { Name = TextureParameterName.TextureBaseLevel, Value = 0 },
            new() { Name = TextureParameterName.TextureMaxLevel, Value = 0 }
        ]
    };

    private PostTarget? scene, floodA, floodB, field, historyA, historyB, result;
    private ScreenTechnique? scenePass, floodPass, distancePass, tracePass, denoisePass;

    private int frame;
    private Vector2? originBefore;

    /// <summary>What the tracer found last, for the deferred pass. Null before the first frame.</summary>
    public Texture? Result => result?.Texture;

    /// <summary>The size of the small picture everything is worked out at.</summary>
    public Vector2 Size { get; private set; }

    /// <summary>
    /// Runs the passes for a frame. Render thread, with the G-buffer drawn and the camera block set to the camera.
    /// </summary>
    internal void Run(DeferredRenderer2D renderer, Camera camera, float dt)
    {
        uint width = (uint)MathF.Max(1.0f, MathF.Ceiling(renderer.ViewportSize.X * Math.Clamp(Scale, 0.1f, 1.0f)));
        uint height = (uint)MathF.Max(1.0f, MathF.Ceiling(renderer.ViewportSize.Y * Math.Clamp(Scale, 0.1f, 1.0f)));
        bool fresh = Fit(width, height);
        Size = new Vector2(width, height);

        scenePass ??= new ScreenTechnique("gi_scene", "shaders/lighting/gi_scene.frag");
        floodPass ??= new ScreenTechnique("gi_flood", "shaders/lighting/gi_flood.frag");
        distancePass ??= new ScreenTechnique("gi_distance", "shaders/lighting/gi_distance.frag");
        tracePass ??= new ScreenTechnique("gi_trace", "shaders/lighting/gi_trace.frag");
        denoisePass ??= new ScreenTechnique("gi_denoise", "shaders/lighting/gi_denoise.frag");

        if (!scenePass.IsValid || !floodPass.IsValid || !distancePass.IsValid || !tracePass.IsValid || !denoisePass.IsValid)
            return;

        frame++;
        var device = Horizon.Graphics.GraphicsDevice.Current;

        // 1. What gives off light and what stops it, at the small size
        scenePass.Bind();
        renderer.FrameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment0, UNIT_ALBEDO);
        renderer.FrameBuffer.BindAttachment(FramebufferAttachment.ColorAttachment1, UNIT_SURFACE);
        result!.Texture.Bind(UNIT_GI_PREVIOUS);
        renderer.BindLighting(scenePass);
        scenePass.SetUniform("uBounce", Math.Clamp(Bounce, 0.0f, 1.5f));
        scenePass.SetUniform("uEmitterRadius", MathF.Max(EmitterRadius, 0.5f));
        scenePass.SetUniform("uEmitterBoost", MathF.Max(EmitterBoost, 0.0f));
        scenePass.SetUniform("uPixelSize", renderer.ShownLightingPixelSize);
        scene!.Bind();
        ScreenTriangle.Draw();

        // 2. The jump flood, seeded from the scene, halving its step until it is one texel
        floodPass.Bind();
        floodPass.SetUniform("uSize", Size);
        scene.Texture.Bind(0);

        floodPass.SetUniform("uStep", 0);
        floodA!.Bind();
        ScreenTriangle.Draw();

        PostTarget from = floodA, into = floodB!;
        for (int step = (int)BitOperations.RoundUpToPowerOf2(Math.Max(width, height)) / 2; step >= 1; step /= 2)
        {
            from.Texture.Bind(1);
            floodPass.SetUniform("uStep", step);
            into.Bind();
            ScreenTriangle.Draw();
            (from, into) = (into, from);
        }

        // 3. Distances out of what the flood found
        distancePass.Bind();
        distancePass.SetUniform("uSize", Size);
        scene.Texture.Bind(0);
        from.Texture.Bind(1);
        field!.Bind();
        ScreenTriangle.Draw();

        // 4. The rays, blended with the last frames moved along with the camera
        Vector2? shift = MeasureShift(camera);
        float keep = fresh || shift is null ? 0.0f : MathF.Pow(Math.Clamp(Smoothing, 0.0f, 0.99f), MathF.Max(dt, 0.0001f) * 60.0f);

        tracePass.Bind();
        scene.Texture.Bind(0);
        field.Texture.Bind(1);
        historyA!.Texture.Bind(2);
        tracePass.SetUniform("uRays", Math.Clamp(Rays, 1, 256));
        tracePass.SetUniform("uSteps", Math.Clamp(Steps, 1, 512));
        tracePass.SetUniform("uMaxDistance", MathF.Max(Reach, 0.01f) * width);
        tracePass.SetUniform("uFrame", frame);
        tracePass.SetUniform("uSize", Size);
        Vector2 shiftValue = shift ?? Vector2.Zero;
        tracePass.SetUniform("uShift", in shiftValue);
        tracePass.SetUniform("uKeep", keep);
        tracePass.SetUniform("uReset", keep <= 0.0f);
        historyB!.Bind();
        ScreenTriangle.Draw();

        (historyA, historyB) = (historyB, historyA);

        // 5. Smoothed, without crossing the walls
        denoisePass.Bind();
        historyA.Texture.Bind(0);
        field.Texture.Bind(1);
        denoisePass.SetUniform("uSize", Size);
        denoisePass.SetUniform("uRadius", MathF.Max(Blur, 0.0f));
        result.Bind();
        ScreenTriangle.Draw();

        denoisePass.Unbind();
    }

    /// <summary>
    /// Helper method for how far the camera moved the picture since the last frame, in texture coordinates, so the
    /// last frame's light can be read from where it is now. Null the first time and after a cut.
    /// </summary>
    private Vector2? MeasureShift(Camera camera)
    {
        Vector4 projected = Vector4.Transform(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), camera.ViewProj);
        var origin = new Vector2(projected.X, projected.Y);

        Vector2? before = originBefore;
        originBefore = origin;

        if (before is not { } was) return null;

        // The screen is two units across the way the GPU counts it, a picture is one
        Vector2 shift = (was - origin) * 0.5f;
        return MathF.Abs(shift.X) > 0.5f || MathF.Abs(shift.Y) > 0.5f ? null : shift;
    }

    /// <summary>
    /// Helper method to have every target be the size that is wanted, made anew when it isn't. True when they were made.
    /// </summary>
    private bool Fit(uint width, uint height)
    {
        if (scene is not null && scene.Fits(width, height)) return false;

        Release();

        scene = new PostTarget(width, height, PostTarget.Precise);
        floodA = new PostTarget(width, height, Seeds);
        floodB = new PostTarget(width, height, Seeds);
        field = new PostTarget(width, height, PostTarget.Precise);
        historyA = new PostTarget(width, height, PostTarget.Precise);
        historyB = new PostTarget(width, height, PostTarget.Precise);
        result = new PostTarget(width, height, PostTarget.Precise);

        // Nothing found yet
        var device = Horizon.Graphics.GraphicsDevice.Current;
        foreach (PostTarget target in new[] { historyA, historyB, result })
            device.ClearColorAttachment(target.FrameBuffer, 0, Vector4.Zero);

        originBefore = null;
        return true;
    }

    private void Release()
    {
        scene?.Dispose(); floodA?.Dispose(); floodB?.Dispose(); field?.Dispose();
        historyA?.Dispose(); historyB?.Dispose(); result?.Dispose();
        scene = floodA = floodB = field = historyA = historyB = result = null;
    }

    public void Dispose() => Release();
}
