using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Transitions;

namespace Horizon.Rendering;

/// <summary>
/// Whatever is in the window by the time this is drawn, blurred, and darkened a little if asked. For a scene that
/// is laid over another one (an options screen over a fight, see <see cref="Scene.Open"/> with
/// <see cref="Components.SceneBelow.Drawn"/>) and wants the one under it for a background without it being in the
/// way of what there is to read. Add it to the scene before anything else that draws.
/// <code>
/// // a pause screen, opened with Open(new PauseScene(), transition, SceneBelow.Drawn)
/// AddEntity(new BlurredBackdrop { Radius = 12, Dim = 0.35f });
/// AddComponent(UICompositor.ForScreen()).CreateModule().LoadLayout("pause.hor");
/// </code>
/// The scene underneath is drawn every frame like always, so what is blurred keeps moving if it does (see
/// <see cref="Components.SceneBelow.Updated"/>), and stands still if it doesn't. It costs a copy of the window and
/// a blur a frame, the same blur the blur transition does.
/// </summary>
public sealed class BlurredBackdrop : GameObject, IDisposable
{
    private const string UNIFORM_COLOR = "uColor";

    private static readonly BlendMode ColoursOnly = new(
        BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
        BlendFactor.Zero, BlendFactor.One);

    private readonly GaussianBlur gaussian = new();
    private PostTechnique? copy, fade;
    private PostTarget? sharp, smeared;

    /// <summary>How far the picture is smeared, in pixels.</summary>
    public float Radius { get; set; } = 14.0f;

    /// <summary>How much of <see cref="Tint"/> is laid over the blurred picture, 0 for none of it and 1 for nothing else.</summary>
    public float Dim { get; set; } = 0.3f;

    /// <summary>What it is darkened with, black unless told otherwise.</summary>
    public Vector3 Tint { get; set; }

    public override void Render(float dt)
    {
        Vector2 size = GameEngine.Instance.WindowManager.ViewportSize;
        if (size.X < 1.0f || size.Y < 1.0f)
            return;

        // The pictures are kept for as long as this is, whichever scene happens to be drawing right now
        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        var before = RenderState.Save();
        RenderState.Blend = false;
        RenderState.DepthTest = false;

        uint width = (uint)size.X, height = (uint)size.Y;
        if (sharp is null || !sharp.Fits(width, height))
        {
            sharp?.Dispose();
            sharp = new PostTarget(width, height);
        }

        sharp.CopyFromWindow();
        PostTarget smear = gaussian.Blur(sharp.Texture, sharp.Size, MathF.Max(Radius, GaussianBlur.FULL_RADIUS), ref smeared);

        copy ??= new PostTechnique("copy");
        copy.Bind();
        smear.Texture.Bind(0);
        Renderer2D.BindOutput(null);
        ScreenTriangle.Draw();
        copy.Unbind();

        if (Dim > 0.0f)
        {
            Vector4 color = new(Tint, Math.Clamp(Dim, 0.0f, 1.0f));

            // Only the colours, whatever the alpha of the window is stays
            RenderState.Blend = true;
            RenderState.BlendMode = ColoursOnly;

            fade ??= new PostTechnique("fade");
            fade.Bind();
            fade.SetUniform(UNIFORM_COLOR, in color);
            ScreenTriangle.Draw();
            fade.Unbind();
        }

        RenderState.Restore(before);

        base.Render(dt);
    }

    // its targets go with the entity, Dispose was a method of its own that hid the entity's and never ran with the rest
    protected override void DisposeOther()
    {
        gaussian.Dispose();
        sharp?.Dispose();
        smeared?.Dispose();
        base.DisposeOther();
    }
}
