using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Brings the picture to a brightness, the way a camera or an eye would. Three things, each on its own knob. An
/// exposure, which is what everything is multiplied by, part of it fixed (<see cref="Exposure"/>) and part of it
/// found from the picture itself every frame and eased over a second or so (<see cref="Adaptation"/>), so a dark
/// street comes up a little once you have been on it a while and a bright noon settles back, the way eyes get
/// used to the dark without turning the night into day. A knee past which the bright end is rolled off towards
/// white instead of cut (<see cref="Knee"/>), so a lamp that is too bright for the screen stays the colour of the
/// lamp. And a gamma (<see cref="Gamma"/>), which over 1 lifts the middle of the range and leaves the ends where
/// they are, which is what takes the harshness out of a lit window on a dark street, the ground round it comes
/// up and the window doesn't.
/// <code>
/// world.PostProcessing.Add(new ExposureEffect { Gamma = 1.2f, Adaptation = 0.4f, MostAdapted = 1.6f });
/// </code>
/// Two passes, a compute one that measures the picture (shaders/post/exposure_measure.slang, one workgroup, next to
/// free) and the one that draws it (exposure.slang). What the eye has got used to is one number in a buffer on the
/// GPU, nothing comes back to the CPU and nothing is allocated a frame. Put it before anything that wants the
/// finished colours (a tube) and after anything that blurs.
/// </summary>
public sealed class ExposureEffect : PostEffect
{
    private const string UNIFORM_EXPOSURE = "uExposure";
    private const string UNIFORM_GAMMA = "uGamma";
    private const string UNIFORM_KNEE = "uKnee";

    private const string UNIFORM_DELTA_TIME = "uDeltaTime";
    private const string UNIFORM_KEY = "uKey";
    private const string UNIFORM_STRENGTH = "uStrength";
    private const string UNIFORM_LEAST = "uLeast";
    private const string UNIFORM_MOST = "uMost";
    private const string UNIFORM_SPEED_DARK = "uSpeedDark";
    private const string UNIFORM_SPEED_LIGHT = "uSpeedLight";
    private const string UNIFORM_RESET = "uReset";

    // Where the shaders have the buffer the adapted exposure lives in
    private const uint ADAPTED_BINDING = 2;

    private static readonly float[] Nothing = [0.0f, 0.0f, 0.0f, 0.0f];

    private PostTechnique draw = null!;
    private Technique? measure;
    private GpuBuffer? adapted;
    private bool reset = true;

    /// <summary>What everything is multiplied by before anything else, 1 for the picture as it is.</summary>
    public float Exposure { get; set; } = 1.0f;

    /// <summary>
    /// The curve of the middle of the range. 1 leaves the picture as it is, over 1 lifts the darks and the mids
    /// towards the light without touching black or white, under 1 sinks them. 1.2 is a gentle lift.
    /// </summary>
    public float Gamma { get; set; } = 1.0f;

    /// <summary>
    /// Where the bright end starts to be rolled off towards white rather than cut, 0 to 1. Everything up to it is
    /// left alone. 1 for no roll off at all.
    /// </summary>
    public float Knee { get; set; } = 0.8f;

    /// <summary>
    /// How far the exposure goes towards bringing the average brightness of the picture to <see cref="Key"/>, 0 for
    /// not at all (the exposure is <see cref="Exposure"/> and that is that) and 1 for all the way, which is night
    /// vision. A half goes halfway, a dark scene comes up some and is still dark.
    /// </summary>
    public float Adaptation { get; set; } = 0.5f;

    /// <summary>The middle grey the average brightness is brought towards, 0.18 the usual.</summary>
    public float Key { get; set; } = 0.18f;

    /// <summary>The least the adapted part of the exposure may be, under 1 and a bright noon is brought down.</summary>
    public float LeastAdapted { get; set; } = 1.0f;

    /// <summary>The most it may be, how far into the dark the eye goes. 1.6 is some, 4 is a cat.</summary>
    public float MostAdapted { get; set; } = 1.6f;

    /// <summary>How quickly the eye gets used to the dark, as a share of the way there a second. Slowly, the way eyes do.</summary>
    public float DarkSpeed { get; set; } = 0.6f;

    /// <summary>How quickly it gets used to the light, which eyes do faster.</summary>
    public float LightSpeed { get; set; } = 1.8f;

    /// <summary>Starts the adaptation again from what the next frame says rather than easing to it, for a cut to somewhere else.</summary>
    public void Reset() => reset = true;

    protected override void Initialize()
    {
        draw = new PostTechnique("exposure");

        var shaders = GameEngine.Instance.ObjectManager.Shaders;
        if (shaders.TryCreateOrGet("post_exposure_measure", ShaderDescription.FromPath("shaders/post", "exposure_measure"), out var made))
            measure = new Technique(made.Asset);

        adapted = GpuBuffer.Create(BufferDescription.StorageBuffer(16));
        adapted.Name = "exposure";
        adapted.Update<float>(Nothing);
    }

    protected override void Render(PostContext context)
    {
        var device = GraphicsDevice.Current;

        // What the eye is used to, out of the picture as it is before this. Off, the buffer holds a 1 and the
        // draw reads that
        if (measure is not null && adapted is not null)
        {
            measure.Bind();
            measure.SetUniform(UNIFORM_DELTA_TIME, context.DeltaTime);
            measure.SetUniform(UNIFORM_KEY, MathF.Max(Key, 0.001f));
            measure.SetUniform(UNIFORM_STRENGTH, Math.Clamp(Adaptation, 0.0f, 1.0f));
            measure.SetUniform(UNIFORM_LEAST, MathF.Max(LeastAdapted, 0.01f));
            measure.SetUniform(UNIFORM_MOST, MathF.Max(MostAdapted, MathF.Max(LeastAdapted, 0.01f)));
            measure.SetUniform(UNIFORM_SPEED_DARK, MathF.Max(DarkSpeed, 0.0f));
            measure.SetUniform(UNIFORM_SPEED_LIGHT, MathF.Max(LightSpeed, 0.0f));
            measure.SetUniform(UNIFORM_RESET, reset);
            measure.BindBuffer(ADAPTED_BINDING, adapted);
            context.Source.Bind(0);
            device.Dispatch(1);
            device.Barrier(BarrierTargets.ShaderStorage);
            measure.Unbind();
            reset = false;
        }

        draw.Bind();
        draw.SetUniform(UNIFORM_EXPOSURE, MathF.Max(Exposure, 0.0f));
        draw.SetUniform(UNIFORM_GAMMA, MathF.Max(Gamma, 0.05f));
        draw.SetUniform(UNIFORM_KNEE, Math.Clamp(Knee, 0.0f, 1.0f));
        if (adapted is not null) draw.BindBuffer(ADAPTED_BINDING, adapted);
        context.Source.Bind(0);
        context.Draw();
        draw.Unbind();
    }

    public override void Dispose()
    {
        adapted?.Dispose();
        adapted = null;
        base.Dispose();
    }
}
