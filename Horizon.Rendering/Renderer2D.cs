using Bogz.Logging;
using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.PostProcessing;
using Horizon.OpenGL;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Rendering;

/// <summary>
/// Class providing a rendering and post processing pipeline for 2D sprite oriented rendering, specializing in extra functionality for pixel art.
/// Everything that is added to it (with AddEntity) is drawn into its frame buffer rather than straight to the window,
/// which is then put on screen by a <see cref="Renderer2DTechnique"/>. Whatever is to stay out of that (a HUD) is simply
/// left outside and drawn after it.
/// On its way to the screen the picture goes through the effects of <see cref="PostProcessing"/>, if there are any:
/// see <see cref="PostProcessor"/>.
/// A renderer can be put inside of another one, which it is then shown in rather than on screen. That is how a
/// world that is lit (a <see cref="DeferredRenderer2D"/> with its own effects) and a HUD that isn't end up behind
/// the same glass: both go into a plain renderer that has the effect of the glass.
/// On its own all this gets is a frame buffer of a size of its own choosing, see <see cref="DeferredRenderer2D"/> for lighting.
/// </summary>
public class Renderer2D : GameObject
{
    public FrameBufferObject FrameBuffer { get => frameBuffer; private set => frameBuffer = value; }
    public RenderRectangle RenderRectangle { get => renderRectangle; private set => renderRectangle = value; }

    /// <summary>The size of the frame buffer everything is drawn into, however big it ends up on screen.</summary>
    public Vector2 ViewportSize { get; init; }

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
    /// Whether the frame buffer holds the picture as it is. If it doesn't, the picture only exists once the
    /// technique has made it out of what is in there, which is what lighting is.
    /// </summary>
    protected internal virtual bool HoldsPicture => true;

    // The renderer that is drawing its children right now, which is what a renderer among those children is shown in
    private static Renderer2D? current;

    /// <summary>
    /// The renderer that is drawing what is in it right now, null while none is: what is drawn then goes straight
    /// to wherever the engine puts the frame.
    /// </summary>
    internal static Renderer2D? Current => current;

    // Made once, so running the effects doesn't allocate
    private readonly Action resolve, bindOutput;
    private Renderer2D? outer;
    private float frameTime;

    protected virtual Renderer2DTechnique CreateTechnique() => new(FrameBuffer);

    protected virtual FrameBufferObject CreateFrameBuffer(in uint width, in uint height) =>
        CreateFrameBuffer(
            new FrameBufferObjectDescription
            {
                Width = width,
                Height = height,
                Attachments = new() {
                    { FramebufferAttachment.ColorAttachment0, FrameBufferAttachmentDefinition.TextureRGBAByteNearest },

                    // Sprite batches cut their sprites out with the stencil
                    { FramebufferAttachment.DepthStencilAttachment, FrameBufferAttachmentDefinition.DepthStencilComponent },
                }
            });

    /// <summary>
    /// Helper method to make a frame buffer, anything going wrong is logged and thrown: there is no drawing without one.
    /// </summary>
    protected static FrameBufferObject CreateFrameBuffer(in FrameBufferObjectDescription description) =>
        FrameBufferObject.Create(description);

    private FrameBufferObject frameBuffer;
    private RenderRectangle renderRectangle;

    public Renderer2D(in uint width, in uint height)
    {
        ViewportSize = new Vector2(width, height);

        resolve = () => RenderRectangle.Render(frameTime);
        bindOutput = () => BindOutput(outer);
    }

    public override void Initialize()
    {
        base.Initialize();

        // i am aware we just went from uint -> float!! -> uint but fuck it we ball.
        FrameBuffer = CreateFrameBuffer((uint)ViewportSize.X, (uint)ViewportSize.Y);

        RenderRectangle = new(CreateTechnique());
        PushToInitializationQueue(renderRectangle);
    }

    public override void Render(float dt)
    {
        // Whatever was just added is set up before anything is bound, setting things up tends to leave bindings behind
        InitializeAll();

        // The rest of the frame (and of the engine) is drawn with whatever it had set, which is put back when we are done
        var before = RenderState.Save();

        // Bind the framebuffer and its attachments
        FrameBuffer.Bind();
        FrameBuffer.Viewport();

        // Everything is flat and drawn back to front, nothing is to be thrown out for being behind something.
        // What is see-through is blended over what is there already, in every attachment alike: the alpha that comes
        // out of that is how much of the pixel is covered.
        RenderState.DepthTest = false;
        RenderState.Blend = true;
        RenderState.BlendMode = BlendMode.Alpha;

        Clear();

        // draw all children. A renderer among them is shown in us, and goes back to whoever we are shown in after
        outer = current;
        current = this;
        base.Render(dt);
        current = outer;

        // What we put on screen replaces what is there, it isn't laid over it
        RenderState.Blend = false;
        frameTime = dt;

        if (PostProcessing.Prepare())
        {
            // Through the effects, the last of which draws to where we are shown
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
            BindOutput(outer);
            RenderRectangle.Render(dt);
        }

        RenderState.Restore(before);
    }

    /// <summary>
    /// GL thread, with the frame buffer bound. Empties every attachment for a new frame.
    /// </summary>
    protected virtual void Clear()
    {
        Clear(0, ClearColor);
        ClearDepthStencil();
    }

    /// <summary>
    /// Helper method to fill a colour attachment of the frame buffer, each one can be emptied to a value of its own.
    /// </summary>
    /// <param name="index">Which colour attachment, in the order the frame buffer draws to them.</param>
    protected unsafe void Clear(int index, Vector4 value)
    {
        Engine.GL.ClearNamedFramebuffer(FrameBuffer.Handle, BufferKind.Color, index, (float*)&value);
    }

    protected void ClearDepthStencil()
    {
        var gl = Engine.GL;

        // Only what can be written to gets cleared, and whoever drew last might have left these off
        gl.DepthMask(true);
        gl.StencilMask(0xFF);
        gl.ClearNamedFramebuffer(FrameBuffer.Handle, GLEnum.DepthStencil, 0, 1.0f, 0);
    }

    /// <summary>
    /// Helper method to bind whatever this renderer is shown in: the renderer it is inside of, or else whatever the
    /// engine is drawing the frame into, which is the window.
    /// </summary>
    internal static void BindOutput(Renderer2D? outer)
    {
        if (outer is not null)
        {
            outer.FrameBuffer.Bind();
            outer.FrameBuffer.Viewport();
            return;
        }

        FrameBufferObject.Unbind();
        Engine.GL.Viewport(0, 0, (uint)Engine.WindowManager.ViewportSize.X, (uint)Engine.WindowManager.ViewportSize.Y);
    }

    /// <summary>
    /// Helper method to say how big what <see cref="BindOutput"/> binds is.
    /// </summary>
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
