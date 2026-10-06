using System.Numerics;

using Horizon.Rendering.PostProcessing;

namespace Horizon.Rendering.Transitions;

/// <summary>
/// The plainest transition there is. The old scene fades to a colour and the new one fades in from it.
/// <code>
/// engine.SceneManager.Transition = new FadeTransition { OutTime = 0.15f, InTime = 0.25f };
/// </code>
/// </summary>
public sealed class FadeTransition : ScreenTransition
{
    private const string UNIFORM_COLOR = "uColor";

    private PostTechnique technique = null!;

    /// <summary>What the scenes fade to and from, black unless told otherwise.</summary>
    public Vector3 Color { get; set; } = Vector3.Zero;

    protected override void Initialize()
    {
        technique = new PostTechnique("fade");
    }

    protected override void Draw(float cover, bool arriving, float dt)
    {
        Vector4 color = new(Color, Math.Clamp(cover, 0.0f, 1.0f));

        technique.Bind();
        technique.SetUniform(UNIFORM_COLOR, in color);
        DrawOverScreen();

        technique.Unbind();
    }
}
