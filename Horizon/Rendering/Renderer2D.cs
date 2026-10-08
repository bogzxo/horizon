using Horizon.Logging;
using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Rendering;

/// <summary>
/// Class providing a rendering and post processing pipeline for 2D sprite oriented rendering, specializing in extra functionality for pixel art.
/// Everything that is added to it (with AddEntity) is drawn into its render target rather than straight to the window,
/// which is then put on screen by a <see cref="Renderer2DTechnique"/>. Whatever is to stay out of that (a HUD) is simply
/// left outside and drawn after it.
/// On its way to the screen the picture goes through the effects of <see cref="PostProcessing"/>, if there are any,
/// see <see cref="PostProcessor"/>.
/// A renderer can be put inside of another one, which it is then shown in rather than on screen. That is how a
/// world that is lit (a <see cref="DeferredRenderer2D"/> with its own effects) and a HUD that isn't end up behind
/// the same glass. Both go into a plain renderer that has the effect of the glass.
/// On its own all this gets is a render target of a size of its own choosing, see <see cref="DeferredRenderer2D"/> for lighting.
/// </summary>
public class Renderer2D : GameObject
{
    public RenderTarget FrameBuffer { get => frameBuffer; private set => frameBuffer = value; }

    /// <summary>What puts the picture where the renderer is shown, made by <see cref="CreateTechnique"/>.</summary>
    public Renderer2DTechnique Technique { get => technique; private set => technique = value; }

    /// <summary>The size of the render target everything is drawn into, however big it ends up on screen.</summary>
    public Vector2 ViewportSize { get; private set; }

    /// <summary>
    /// Whether the renderer keeps itself the size of the window, remade whenever that changes. For a renderer that
    /// draws a pixel a pixel. One that draws pixel art at a size of its own and has it blown up leaves this off.
    /// </summary>
    public bool FollowWindow { get; set; }

    /// <summary>What shows wherever nothing was drawn.</summary>
    public Vector4 ClearColor { get; set; } = new Vector4(0.0f, 0.0f, 0.0f, 1.0f);

    /// <summary>The effects the picture goes through before it is shown, none to begin with.</summary>
    public PostProcessor PostProcessing { get; } = new();

    /// <summary>
    /// How fast everything in the picture is moving across it and how near it is, for the effects that go by that
    /// (see <see cref="DeferredRenderer2D"/> for what is in it). Null for a renderer that doesn't keep track.
    /// </summary>
    public virtual Texture? MotionTexture => null;

    /// <summary>
    /// Whether the render target holds the picture as it is. If it doesn't, the picture only exists once the
    /// technique has made it out of what is in there, which is what lighting is.
    /// </summary>
    protected internal virtual bool HoldsPicture => true;

    // The renderer that is drawing its children right now, which is what a renderer among those children is shown in
    private static Renderer2D? current;

    /// <summary>
    /// The renderer that is drawing what is in it right now, null while none is. What is drawn then goes straight
    /// to wherever the engine puts the frame.
    /// </summary>
    internal static Renderer2D? Current => current;

    // Made once, so running the effects doesn't allocate
    private readonly Action resolve, bindOutput;
    private Renderer2D? outer;
    private float frameTime;

    // What the GPU timings call this renderer, see GraphicsDevice.BeginGpuScope
    private string? scopeLabel;

    /// <summary>Makes what puts the picture on screen, once the render target is there. Render thread.</summary>
    protected virtual Renderer2DTechnique CreateTechnique() => new(FrameBuffer);

    protected virtual RenderTarget CreateFrameBuffer(in uint width, in uint height) =>
        CreateFrameBuffer(
            new RenderTargetDescription
            {
                Width = width,
                Height = height,
                Attachments = new()
                {
                    { AttachmentPoint.Color0, TextureDefinition.RgbaUnsignedByteNearest },

                    // Sprite batches cut their sprites out with the stencil
                    { AttachmentPoint.DepthStencil, TextureDefinition.DepthStencil },
                }
            });

    /// <summary>Helper method to make a render target, anything going wrong is logged and thrown, there is no drawing without one.</summary>
    protected static RenderTarget CreateFrameBuffer(in RenderTargetDescription description) => RenderTarget.Create(description);

    private RenderTarget frameBuffer = null!;
    private Renderer2DTechnique technique = null!;

    public Renderer2D(in uint width, in uint height)
    {
        ViewportSize = new Vector2(width, height);

        resolve = Resolve;
        bindOutput = () => BindOutput(outer);
    }

    /// <summary>Helper method to draw the picture into whatever is bound with the technique, one triangle over all of it.</summary>
    private void Resolve()
    {
        technique.Bind();
        PostProcessing.DrawScreen();
        technique.Unbind();
    }

    public override void Initialize()
    {
        base.Initialize();

        // i am aware we just went from uint -> float!! -> uint but fuck it we ball.
        FrameBuffer = CreateFrameBuffer((uint)ViewportSize.X, (uint)ViewportSize.Y);
        Technique = CreateTechnique();
    }

    /// <summary>
    /// Makes the renderer another size, render target and all. Render thread. What was drawn into it is gone, the next
    /// frame fills it again, and the post effects remake their targets to fit by themselves.
    /// </summary>
    public void Resize(uint width, uint height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (frameBuffer is not null && width == frameBuffer.Width && height == frameBuffer.Height) return;

        frameBuffer?.Dispose();
        ViewportSize = new Vector2(width, height);
        FrameBuffer = CreateFrameBuffer(width, height);
        Technique = CreateTechnique();
        Resized();
    }

    /// <summary>Called after <see cref="Resize"/> made the render target anew, for whatever else goes by its size.</summary>
    protected virtual void Resized()
    { }

    public override void Render(float dt)
    {
        // Whatever was just added is set up before anything is bound, setting things up tends to leave bindings behind.
        // Drawn alongside the simulation that has happened already, with it standing still, before the frame began
        if (!RenderFrame.Active.IsDecoupled)
            InitializeAll();

        if (FollowWindow)
        {
            Vector2 window = Engine.WindowManager.ViewportSize;
            Resize((uint)window.X, (uint)window.Y);
        }

        // The rest of the frame (and of the engine) is drawn with whatever it had set, which is put back when we are done
        var before = RenderState.Save();

        using var scope = Engine.Graphics.BeginGpuScope(scopeLabel ??= string.IsNullOrEmpty(Name) ? GetType().Name : Name);

        FrameBuffer.Bind();

        // Everything is flat and drawn back to front, nothing is to be thrown out for being behind something.
        // What is see-through is blended over what is there already, in every attachment alike, the alpha that comes
        // out of that is how much of the pixel is covered
        RenderState.DepthTest = false;
        RenderState.Blend = true;
        RenderState.BlendMode = BlendMode.Alpha;

        Clear();

        // Draw all children. A renderer among them is shown in us, and goes back to whoever we are shown in after
        outer = current;
        current = this;
        base.Render(dt);
        current = outer;

        // What we put on screen replaces what is there, it isn't laid over it
        RenderState.Blend = false;
        frameTime = dt;

        // Whatever a renderer works out of the render target before the picture goes anywhere (the lighting)
        BeforeResolve(dt);

        if (PostProcessing.Prepare())
        {
            // Through the effects, the last of which draws to where we are shown
            using var effects = Engine.Graphics.BeginGpuScope("post effects");
            PostProcessing.Run(
                ViewportSize,
                MotionTexture,
                dt,
                HoldsPicture ? FrameBuffer.Color : null,
                resolve,
                bindOutput,
                OutputSize(outer));
        }
        else
        {
            // Straight to where we are shown
            using var shown = Engine.Graphics.BeginGpuScope("resolve");
            BindOutput(outer);
            Resolve();
        }

        RenderState.Restore(before);
    }

    /// <summary>
    /// Render thread, after everything in the renderer has been drawn into the render target and before the picture
    /// is put where it is shown. For passes that work out something of the target first (the path traced
    /// lighting). Whatever is bound afterwards doesn't matter, the output is bound after this.
    /// </summary>
    protected virtual void BeforeResolve(float dt)
    { }

    /// <summary>Render thread, with the render target bound. Empties every attachment for a new frame.</summary>
    protected virtual void Clear()
    {
        Clear(0, ClearColor);
        ClearDepthStencil();
    }

    /// <summary>Helper method to fill a colour attachment of the render target, each one can be emptied to a value of its own.</summary>
    /// <param name="index">Which colour attachment, in the order the target draws to them.</param>
    protected void Clear(int index, Vector4 value) => Engine.Graphics.ClearColorAttachment(FrameBuffer, index, value);

    protected void ClearDepthStencil() => Engine.Graphics.ClearDepthStencil(FrameBuffer);

    /// <summary>
    /// Helper method to bind whatever this renderer is shown in, the renderer it is inside of, or else whatever the
    /// engine is drawing the frame into, which is the window.
    /// </summary>
    internal static void BindOutput(Renderer2D? outer)
    {
        if (outer is not null)
        {
            outer.FrameBuffer.Bind();
            return;
        }

        Engine.Graphics.BindWindow();
    }

    /// <summary>Helper method to say how big what <see cref="BindOutput"/> binds is.</summary>
    internal static Vector2 OutputSize(Renderer2D? outer)
    {
        if (outer is not null)
            return outer.ViewportSize;

        return Engine.WindowManager.ViewportSize;
    }

    protected override void DisposeOther()
    {
        PostProcessing.Dispose();

        frameBuffer?.Dispose();

        base.DisposeOther();
    }
}
