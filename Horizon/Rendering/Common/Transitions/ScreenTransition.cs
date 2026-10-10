using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Rendering.Transitions;

/// <summary>
/// A scene transition that works on the finished frame, which is most of them (a fade, a blur, a dissolve).
/// By the time one of these gets to draw, the scene is in the window with everything it has.
/// It can draw over that, or take a copy of it (<see cref="Grab"/>) and put something else in its place.
/// <para>
/// Writing one is like writing a post effect. Make the shaders in <see cref="Initialize"/>. In <see cref="Draw"/> bind one, set its uniforms
/// and end with <see cref="DrawToScreen"/> or <see cref="DrawOverScreen"/>. The shaders are the ones in shaders/post, see <see cref="PostTechnique"/>.
/// </para>
/// Whatever was blended and tested when the scene was done drawing is put back afterwards, the rest of the frame doesn't notice.
/// </summary>
public abstract class ScreenTransition : SceneTransition, IDisposable
{
    private bool initialized;
    private PostTarget? frame;

    /// <summary>The size of the window, in pixels.</summary>
    protected static Vector2 ScreenSize => GameEngine.Instance.WindowManager.ViewportSize;

    /// <summary>Render thread, once, before the transition first draws. Where its shaders are made.</summary>
    protected virtual void Initialize()
    { }

    /// <summary>
    /// Render thread, once a frame for as long as the transition runs. Nothing is blended and nothing is tested, what is drawn replaces what is there.
    /// See <see cref="SceneTransition.Render"/> for what the cover is and when a scene is arriving.
    /// </summary>
    protected abstract void Draw(float cover, bool arriving, float dt);

    private static readonly BlendMode ColoursOnly = new(
        BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
        BlendFactor.Zero, BlendFactor.One);

    public sealed override void Render(float cover, bool arriving, float dt)
    {
        // A transition outlives every scene, what it makes on the GPU isn't the scene's to free
        using var nobody = Horizon.Content.AssetScope.EnterGlobal();

        var before = RenderState.Save();

        RenderState.Blend = false;
        RenderState.DepthTest = false;

        if (!initialized)
        {
            initialized = true;
            Initialize();
        }

        Draw(cover, arriving, dt);

        // Back to the window, with everything the way it was found
        Renderer2D.BindOutput(null);

        RenderState.Restore(before);
    }

    /// <summary>
    /// Takes a copy of the frame as it is right now, which is the scene with nothing done to it yet.
    /// The copy is this transition's own and is good until the next one is taken.
    /// </summary>
    protected PostTarget Grab()
    {
        Vector2 size = ScreenSize;
        uint width = (uint)size.X, height = (uint)size.Y;

        if (frame is null || !frame.Fits(width, height))
        {
            frame?.Dispose();
            frame = new PostTarget(width, height);
        }

        frame.CopyFromWindow();
        return frame;
    }

    /// <summary>
    /// Draws over the whole of the window with whatever technique is bound, replacing what is there.
    /// </summary>
    protected static void DrawToScreen()
    {
        Renderer2D.BindOutput(null);
        ScreenTriangle.Draw();
    }

    /// <summary>
    /// Lays whatever the bound technique draws over what is in the window, as see-through as its alpha says.
    /// </summary>
    protected static void DrawOverScreen()
    {
        // The alpha of the window is left the way it is, only the colours are covered
        RenderState.Blend = true;
        RenderState.BlendMode = ColoursOnly;

        DrawToScreen();
        RenderState.Blend = false;
    }

    /// <summary>
    /// Draws over the whole of a target of the transition's own, with whatever technique is bound.
    /// </summary>
    protected static void DrawTo(PostTarget target)
    {
        target.Bind();
        ScreenTriangle.Draw();
    }

    /// <summary>
    /// Told the transition that just ended is over. The copy of the frame is kept for the next time (it is made anew
    /// when the window is another size), making it at the start of every transition is a hitch right when the player
    /// pressed something. See <see cref="Dispose"/> for letting go of it. Whoever overrides this calls this one as well.
    /// </summary>
    public override void Finish()
    { }

    /// <summary>
    /// Lets go of every picture the transition keeps between one time and the next, for a transition that isn't going
    /// to be used again. Render thread, and not while it runs. Whoever overrides this to let go of more calls this one as well.
    /// </summary>
    public virtual void Dispose()
    {
        frame?.Dispose();
        frame = null;
    }
}
