using Bogz.Logging;
using System.Numerics;

using Horizon.Core.Tweening;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// Something that is done to the picture a <see cref="Renderer2D"/> has drawn before it is put on screen: a blur, a
/// grade, the look of an old screen. Effects are added to <see cref="Renderer2D.PostProcessing"/> and run in the
/// order they are in, each on what the one before it left. What is laid over everything else instead (a UI) has
/// effects of its own the same way, see <see cref="PostLayer"/>.
/// An effect is handed a <see cref="PostContext"/> with the picture so far and draws what it makes of it with
/// <see cref="PostContext.Draw()"/>, once. Whatever it needs along the way (a smaller copy, a pass of its own)
/// it draws into a <see cref="PostTarget"/> of its own first.
/// </summary>
public abstract class PostEffect : IDisposable
{
    private bool initialized;

    /// <summary>Whether the effect is run. One that is off costs nothing, the picture goes straight past it.</summary>
    public bool Enabled { get; set; } = true;

    private TweenContext? tweens;

    /// <summary>
    /// The tweens that are animating this effect, for easing a setting of it instead of snapping it.
    /// They are moved along once a frame for as long as the effect is on, so whatever starts one switches the effect on first.
    /// </summary>
    public TweenContext Tweens => tweens ?? Interlocked.CompareExchange(ref tweens, new TweenContext(), null) ?? tweens;

    /// <summary>
    /// Whether the effect only does anything to what moves. One that says so is left out for as long as whoever
    /// drew the picture knows that nothing in it does, which saves all of its passes.
    /// </summary>
    protected internal virtual bool NeedsMotion => false;

    /// <summary>GL thread, once, before the effect is first run: where its shaders are made.</summary>
    protected virtual void Initialize()
    { }

    /// <summary>
    /// GL thread, every frame the effect is on. Nothing is blended and nothing is tested while effects run: what is
    /// drawn replaces what is there.
    /// </summary>
    protected abstract void Render(PostContext context);

    internal void Run(PostContext context)
    {
        if (!initialized)
        {
            initialized = true;
            Initialize();
        }

        tweens?.Tick(context.DeltaTime);
        Render(context);
    }

    /// <summary>Frees what the effect made on the GPU. The renderer does this for the effects it still has when it goes.</summary>
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// What an effect has to work with for a frame, see <see cref="PostEffect"/>.
/// </summary>
public sealed class PostContext
{
    internal PostProcessor Processor = null!;

    // Where the result of the effect goes: a target of the chain, or (for the last effect) wherever the renderer is shown
    internal PostTarget? Into;
    internal Action Output = null!;

    /// <summary>
    /// The picture so far: what was drawn, with whatever the effects before this one did to it. Its alpha is how
    /// much of every pixel there is and its colours come multiplied by that already: all of it for the picture of
    /// a renderer, but a <see cref="PostLayer"/> is see-through wherever nothing was drawn. An effect that is to
    /// work on both passes the alpha on, and treats it like the colours.
    /// </summary>
    public Texture Source { get; internal set; } = null!;

    /// <summary>
    /// How fast everything in the picture is moving across it and how near it is (see <see cref="DeferredRenderer2D"/>
    /// for what is in it), for the effects that go by that. Null if whoever drew the picture doesn't keep track.
    /// </summary>
    public Texture? Motion { get; internal set; }

    /// <summary>The size of <see cref="Source"/> in pixels.</summary>
    public Vector2 SourceSize { get; internal set; }

    /// <summary>
    /// The size of what <see cref="Draw()"/> draws into. For the last effect that is whatever the renderer is shown
    /// on (the window, say), which need not be the size it was drawn at.
    /// </summary>
    public Vector2 OutputSize { get; internal set; }

    /// <summary>How long the frame is, in seconds.</summary>
    public float DeltaTime { get; internal set; }

    /// <summary>
    /// Draws over the whole of where the result of the effect goes, with whatever technique is bound. Every effect
    /// ends with this.
    /// </summary>
    public void Draw()
    {
        if (Into is not null)
            Into.Bind();
        else
            Output();

        Processor.DrawScreen();
    }

    /// <summary>Draws over the whole of a target of the effect's own, with whatever technique is bound.</summary>
    public void Draw(PostTarget target)
    {
        target.Bind();
        Processor.DrawScreen();
    }

    /// <summary>
    /// Whether the effect passed the picture on as it was without drawing anything (see <see cref="Copy"/>): the next
    /// effect reads the same picture this one was handed.
    /// </summary>
    internal bool PassedOn { get; set; }

    /// <summary>Passes the picture on as it is, for an effect that finds it has nothing to do this frame.</summary>
    public void Copy()
    {
        // Another effect comes after this one: it simply reads the picture this one was handed, which costs nothing.
        // Only the last effect has to put the picture where it is shown
        if (Into is not null)
        {
            PassedOn = true;
            return;
        }

        PostTechnique copy = Processor.CopyTechnique;

        copy.Bind();
        Source.Bind(0);
        copy.SetUniform(PostTechnique.UNIFORM_SOURCE, 0);

        Draw();
        copy.Unbind();
    }
}

/// <summary>
/// The shader of a pass of an effect: a fragment shader out of shaders/post, run for every pixel of what is drawn to.
/// It is handed where it is as <c>texCoords</c> (0 to 1 from the bottom left corner), everything else is up to the
/// uniforms the effect sets after binding it.
/// </summary>
public class PostTechnique : Technique
{
    /// <summary>What every pass calls the picture it works on.</summary>
    public const string UNIFORM_SOURCE = "uSource";

    private const string DIRECTORY = "shaders/post";
    private const string VERTEX_SHADER = "screen.vert";

    /// <param name="fragment">The name of the fragment shader in shaders/post, without its extension.</param>
    public PostTechnique(string fragment)
    {
        // By name, so every renderer that uses the effect shares the one shader
        bool created = GameEngine.Instance.ObjectManager.Shaders.TryCreateOrGet(
            $"post_{fragment}",
            new ShaderDescription
            {
                Definitions =
                [
                    new ShaderDefinition(ShaderType.VertexShader, Path.Combine(DIRECTORY, VERTEX_SHADER), string.Empty),
                    new ShaderDefinition(ShaderType.FragmentShader, Path.Combine(DIRECTORY, $"{fragment}.frag"), string.Empty)
                ]
            },
            out var result);

        if (created)
            SetShader(result.Asset);
        else
            Log.Error(result.Message);
    }
}
