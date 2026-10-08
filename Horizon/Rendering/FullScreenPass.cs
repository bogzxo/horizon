using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Rendering;

/// <summary>
/// Draws over the whole of whatever is bound with a technique, once a frame: a background made in a shader, a renderer
/// put on screen. The technique's vertex stage is shaders/common/screen.vert (see <see cref="ScreenTechnique"/> for a
/// technique made out of a fragment shader alone), which hands the fragment shader where it is as <c>texCoords</c>.
/// <code>
/// renderer.AddEntity(new FullScreenPass(new WallTechnique()));
/// </code>
/// </summary>
public class FullScreenPass : GameObject
{
    public Technique Technique { get; set; }

    public FullScreenPass(Technique technique)
    {
        Technique = technique;
    }

    public override void Render(float dt)
    {
        base.Render(dt);

        if (!Technique.IsValid) return;

        Technique.Bind();
        ScreenTriangle.Draw();
        Technique.Unbind();
    }
}
