using Horizon.Core.Tweening;
using Horizon.Graphics;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Rendering.Transitions;

/// <summary>
/// One scene melts into the next. The old scene blurs out, turns into the new one while both are a smear, and the new one comes into focus.
/// It is quick and there is no frame where anything jumps, the last frame of the old scene and the first of the new are the very same picture.
/// <code>
/// engine.SceneManager.Transition = new BlurTransition();
/// </code>
/// Both scenes keep moving under it, nothing is frozen but the moment of the swap itself.
/// </summary>
public sealed class BlurTransition : ScreenTransition
{
    private const string UNIFORM_OTHER = "uOther";
    private const string UNIFORM_AMOUNT = "uAmount";

    // How far the new scene is uncovered by the time the old one has faded out of the smear for good
    private const float OLD_SCENE_GONE = 0.5f;

    private readonly GaussianBlur gaussian = new();
    private PostTechnique mix = null!;

    // The scene on screen blurred, what the old scene left behind, and the two of them on top of each other
    private PostTarget? current, outgoing, blended;

    // How much of the smear is still the scene that was left
    private float oldShare;

    /// <summary>How far the picture is smeared at the height of it (the moment the scenes are swapped), in pixels.</summary>
    public float Radius { get; set; } = 16.0f;

    public BlurTransition()
    {
        // Out in a hurry so the press of a button is felt right away, in a little slower so there is time to see what turned up
        OutTime = 0.12f;
        InTime = 0.28f;
        OutEasing = Easing.OutQuad;
        InEasing = Easing.InOutSine;
    }

    protected override void Initialize()
    {
        mix = new PostTechnique("mix");
    }

    protected override void Draw(float cover, bool arriving, float dt)
    {
        PostTarget sharp = Grab();

        // All the way covered has to be a real blur. With any less the small picture that is kept of the old scene couldn't pass for it
        float radius = MathF.Max(Radius, GaussianBlur.FULL_RADIUS) * Math.Clamp(cover, 0.0f, 1.0f);
        PostTarget smear = gaussian.Blur(sharp.Texture, sharp.Size, radius, ref current);

        // The old scene only ever fades out of the smear. It doesn't come back if the cover goes up again half way,
        // which is somebody leaving a scene they only just got to
        if (arriving)
            oldShare = MathF.Min(oldShare, OldShareAt(cover));

        if (oldShare > 0.0f && outgoing is not null)
        {
            Mix(smear.Texture, outgoing.Texture, oldShare);
            DrawTo(GaussianBlur.Fit(ref blended, sharp.Size));
            smear = blended!;
        }

        // A blur that has barely started is laid over the sharp scene rather than shown in its place, see GaussianBlur
        Mix(sharp.Texture, smear.Texture, GaussianBlur.Share(radius));
        DrawToScreen();
        mix.Unbind();

        // The last anybody sees of the old scene. What is on screen right now is what the new one has to come out of
        if (!arriving && cover >= 1.0f)
        {
            if (ReferenceEquals(smear, blended))
                (outgoing, blended) = (blended, outgoing);
            else
                (outgoing, current) = (current, outgoing);

            oldShare = 1.0f;
        }
    }

    /// <summary>
    /// Helper method to say how much of the smear is the old scene for how far the new one is uncovered.
    /// All of it at first, none of it by the time the picture starts to come into focus.
    /// </summary>
    private static float OldShareAt(float cover)
    {
        float left = Math.Clamp((cover - OLD_SCENE_GONE) / (1.0f - OLD_SCENE_GONE), 0.0f, 1.0f);
        return Ease.Apply(Easing.InOutSine, left);
    }

    /// <summary>
    /// Helper method to bind the shader that lays one picture over another, by an amount from 0 for only the first to 1 for only the other.
    /// </summary>
    private void Mix(Texture picture, Texture other, float amount)
    {
        mix.Bind();
        picture.Bind(0);
        other.Bind(1);
        mix.SetUniform(UNIFORM_AMOUNT, amount);
    }

    public override void Finish()
    {
        // The pictures it blurs into are kept for the next time: making them anew is a hitch at the start of every
        // transition, keeping them is a few pictures of half the size of the screen
        oldShare = 0.0f;

        base.Finish();
    }

    public override void Dispose()
    {
        gaussian.Dispose();

        current?.Dispose();
        outgoing?.Dispose();
        blended?.Dispose();
        current = outgoing = blended = null;

        base.Dispose();
    }
}
