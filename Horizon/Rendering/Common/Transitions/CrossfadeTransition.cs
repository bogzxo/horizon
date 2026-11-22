using Horizon.Core.Tweening;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Rendering.Transitions;

/// <summary>
/// The old scene's last frame laid over the new one and faded out, nothing covered up first. The press is answered on the
/// next frame, the new scene is under it from the start, and nothing in between is ever a colour of its own: the old
/// picture stays whole until the new scene is drawn whole (see <see cref="Horizon.Engine.Components.SceneManager.Cover"/>), then gives way to
/// it. With an <see cref="SceneTransition.InTime"/> of 0 there's no fade at all, only that, a swap with no frame missing.
/// For going between two faces of one program rather than between levels of a game.
/// <code>
/// engine.SceneManager.SetScene(next, new CrossfadeTransition { InTime = 0.18f });
/// </code>
/// </summary>
public sealed class CrossfadeTransition : ScreenTransition
{
    private const string UNIFORM_AMOUNT = "uAmount";

    private PostTechnique mix = null!;

    // The old scene's last frame, and the new one as it's drawn
    private PostTarget? outgoing, current;
    private bool kept;

    public CrossfadeTransition()
    {
        OutTime = 0.0f;
        InTime = 0.18f;
        OutEasing = Easing.Linear;
        InEasing = Easing.OutCubic;
    }

    protected override void Initialize()
    {
        mix = new PostTechnique("mix");
    }

    protected override void Draw(float cover, bool arriving, float dt)
    {
        if (!arriving)
        {
            // The last anybody sees of the old scene, kept as it went out. Nothing is drawn over it
            if (cover >= 1.0f)
            {
                Grab(ref outgoing);
                kept = true;
            }
            return;
        }

        if (!kept || outgoing is null)
            return;

        PostTarget now = Grab(ref current);
        mix.Bind();
        now.Texture.Bind(0);
        outgoing.Texture.Bind(1);
        mix.SetUniform(UNIFORM_AMOUNT, Math.Clamp(cover, 0.0f, 1.0f));
        DrawToScreen();
        mix.Unbind();
    }

    public override void Finish()
    {
        // The pictures are kept for the next time, made anew they were a hitch at the press of the key
        kept = false;
        base.Finish();
    }

    public override void Dispose()
    {
        outgoing?.Dispose();
        outgoing = null;
        current?.Dispose();
        current = null;
        base.Dispose();
    }
}
