using Horizon.Core.Tweening;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Blurs the whole picture, for when something else is meant to be looked at (a menu over a paused game, a versus screen over the arena).
/// <para>
/// To ease it in and out use <see cref="BlurTo"/>, which also switches the effect on while it is needed and off again once the picture is sharp:
/// <code>
/// var blur = renderer.PostProcessing.Add(new BlurEffect { Radius = 0, Enabled = false });
/// blur.BlurTo(12, 0.2f);    // something popped up over the game
/// blur.BlurTo(0, 0.4f);     // and it is gone again
/// </code>
/// It comes on and goes off without a pop. A blur that has only just started is blended with the sharp picture, so there is no frame where it jumps.
/// </para>
/// </summary>
public sealed class BlurEffect : PostEffect
{
    private const string UNIFORM_OTHER = "uOther";
    private const string UNIFORM_AMOUNT = "uAmount";

    // Below this there is nothing to see, and the picture is passed on as it is
    private const float MIN_RADIUS = 0.05f;

    // Only one tween moves the radius at a time, a new one takes over from the old
    private const string RADIUS_CHANNEL = "radius";

    private readonly GaussianBlur gaussian = new();
    private PostTechnique mix = null!;
    private PostTarget? blurred;

    /// <summary>How far the picture is smeared, in pixels of the picture. Zero is no blur at all.</summary>
    public float Radius { get; set; } = 8.0f;

    protected override void Initialize()
    {
        mix = new PostTechnique("mix");
    }

    /// <summary>
    /// Eases the blur to another radius. The effect is switched on for it, and switches itself off again if it ends up with nothing left to blur.
    /// From any thread.
    /// </summary>
    /// <param name="radius">How far the picture is smeared once it gets there, zero to clear the blur up.</param>
    /// <param name="duration">How long it takes, in seconds.</param>
    public Tween BlurTo(float radius, float duration, Easing easing = Easing.OutCubic)
    {
        Enabled = true;

        return Tweens.Play(
            Tween.To(() => Radius, value => Radius = value, MathF.Max(0.0f, radius), duration)
                .SetEasing(easing)
                .OnComplete(() => Enabled = Radius >= MIN_RADIUS),
            RADIUS_CHANNEL);
    }

    protected override void Render(PostContext context)
    {
        float radius = MathF.Max(0.0f, Radius);
        if (radius < MIN_RADIUS)
        {
            context.Copy();
            return;
        }

        gaussian.Blur(context.Source, context.SourceSize, radius, ref blurred);

        // Over the sharp picture, which is all that shows of it once the blur is big enough to matter
        mix.Bind();
        context.Source.Bind(0);
        blurred!.Texture.Bind(1);
        mix.SetUniform(UNIFORM_AMOUNT, GaussianBlur.Share(radius));
        context.Draw();

        mix.Unbind();
    }

    public override void Dispose()
    {
        gaussian.Dispose();
        blurred?.Dispose();
        blurred = null;

        base.Dispose();
    }
}
