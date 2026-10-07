using Bogz.Logging;
using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.PostProcessing;

/// <summary>
/// A see-through picture of its own for what is drawn on top of everything else (a UI, say) and is to have effects
/// to itself: a HUD that blurs as it slides in, without the game under it being touched. Whatever is drawn between
/// <see cref="Begin"/> and <see cref="End"/> goes onto the layer instead of where it was headed, through the
/// <see cref="Effects"/>, and is then laid over what was there:
/// <code>
/// bool layered = layer.Begin();
/// batch.Draw(...);
/// if (layered) layer.End(dt);
/// </code>
/// With no effect on, <see cref="Begin"/> says no and does nothing: the drawing goes where it always went and the
/// layer costs nothing, not even the memory. The layer is colours and nothing else, it has no depth and no stencil:
/// sprites that are cut out with a mask are drawn whole on it.
/// The layer keeps track of what moves on it the way a <see cref="DeferredRenderer2D"/> does (whatever draws with
/// the sprite, tile map or particle shaders says how fast it is going), so a <see cref="MotionBlurEffect"/> works
/// here as it does there. An effect has to pass the alpha of the picture on to be of any use on a layer, see
/// <see cref="PostContext.Source"/>: one that makes all of the picture solid (<see cref="CrtEffect"/>) hides
/// everything under it.
/// <para>
/// A layer can also be what keeps a picture between frames. Begun with <c>retain</c> it is drawn onto whether
/// there are effects or not, and for as long as what would be drawn onto it stays the same <see cref="Replay"/>
/// lays the picture it has over the frame again without any of it being drawn anew: one quad, however much is in it.
/// </para>
/// All of it on the GL thread.
/// </summary>
public sealed class PostLayer : IDisposable
{
    // Fragment shaders write how fast they are going to their fourth output, see DeferredRenderer2D
    private const FramebufferAttachment MOTION_ATTACHMENT = FramebufferAttachment.ColorAttachment3;
    private const int MOTION_OUTPUT = 3;

    // Nothing there, and nothing moving: must match encodeMotion in the shaders that write it
    private static readonly Vector4 Empty = Vector4.Zero;
    private static readonly Vector4 Still = new(128.0f / 255.0f, 128.0f / 255.0f, 0.0f, 0.0f);

    private static readonly Action Nothing = static () => { };

    private readonly Action bindOutput;
    private FrameBufferObject? frameBuffer;
    private Vector2 size;
    private bool unavailable;

    // Where the layer ends up: the renderer that was drawing when it was begun, or the frame itself
    private Renderer2D? target;

    // Whether any effect is on for what is being laid over right now, and whether the layer has a picture to lay over at all
    private bool effectsActive;
    private bool hasPicture;

    // What was set before the layer was begun, put back when it ends
    private RenderState.Saved before;

    /// <summary>The effects the layer goes through before it is laid over what is under it, none to begin with.</summary>
    public PostProcessor Effects { get; } = new();

    public PostLayer()
    {
        bindOutput = () =>
        {
            Renderer2D.BindOutput(target);

            // The colours of the layer come multiplied by how much of them there is, so they are added as they are
            // to what is left of what is underneath
            RenderState.Blend = true;
            RenderState.BlendMode = BlendMode.Premultiplied;
        };
    }

    /// <summary>
    /// Has everything that is drawn from here to <see cref="End"/> go onto the layer, if any effect is on.
    /// </summary>
    /// <param name="moving">
    /// False if nothing of what is about to be drawn moves, for whoever knows: the effects that are only for what
    /// moves are left out then, and if those are all there is so is the layer.
    /// </param>
    /// <param name="retain">
    /// True to have the layer drawn onto even with no effect on, for whoever wants the picture kept to be laid
    /// over again later (see <see cref="Replay"/>).
    /// </param>
    /// <returns>
    /// Whether it does. If not, nothing was changed and there is no <see cref="End"/> to call: whatever is drawn goes
    /// where it was going. That is also what happens inside of a renderer that lights its picture, where there is
    /// no picture to lay anything over yet.
    /// </returns>
    public bool Begin(bool moving = true, bool retain = false)
    {
        hasPicture = false;

        effectsActive = Effects.Prepare(moving);
        if (!effectsActive && !retain)
            return false;

        Renderer2D? into = Renderer2D.Current;
        if (into is { HoldsPicture: false } || !Fit(Renderer2D.OutputSize(into)))
            return false;

        target = into;

        // To be put back by Compose
        before = RenderState.Save();

        frameBuffer!.Bind();
        frameBuffer.Viewport();

        // Drawn back to front and blended, like everything flat. With the alpha kept apart what comes out is how
        // much of every pixel is covered, and colours that are multiplied by it.
        RenderState.DepthTest = false;
        RenderState.Blend = true;
        RenderState.BlendMode = BlendMode.Alpha;

        Clear();
        return true;
    }

    /// <summary>
    /// Puts what was drawn since <see cref="Begin"/> through the effects and lays it over where it would have gone,
    /// which is what is drawn into again afterwards.
    /// </summary>
    /// <param name="dt">How long the frame is, in seconds.</param>
    public void End(float dt)
    {
        if (frameBuffer is null)
            return;

        hasPicture = true;
        Compose(dt);
    }

    /// <summary>
    /// Lays the picture the layer was last drawn with over where it would go once more, through whatever effects
    /// are on, without anything being drawn onto the layer: for a frame in which nothing of it has changed.
    /// </summary>
    /// <param name="dt">How long the frame is, in seconds.</param>
    /// <returns>
    /// False if there is no picture to lay over, or what it would be laid over is another size by now: nothing
    /// was done then, and the layer has to be drawn onto again.
    /// </returns>
    public bool Replay(float dt)
    {
        if (!hasPicture || frameBuffer is null)
            return false;

        Renderer2D? into = Renderer2D.Current;
        if (into is { HoldsPicture: false })
            return false;

        Vector2 wanted = Renderer2D.OutputSize(into);
        if ((uint)MathF.Max(1.0f, wanted.X) != frameBuffer.Width || (uint)MathF.Max(1.0f, wanted.Y) != frameBuffer.Height)
            return false;

        target = into;
        before = RenderState.Save();

        // Nothing in a picture that was kept moves
        effectsActive = Effects.Prepare(false);
        Compose(dt);
        return true;
    }

    /// <summary>
    /// Helper method to put the picture of the layer over where it goes, through the effects if any are on, and
    /// leave everything the way it was found.
    /// </summary>
    private void Compose(float dt)
    {
        var picture = frameBuffer!.Color;

        // Effects replace what they draw over, all but the last one. That one is blended, see bindOutput
        RenderState.Blend = false;
        RenderState.DepthTest = false;

        if (effectsActive)
        {
            Effects.Run(
                size,
                frameBuffer.Attachments[MOTION_ATTACHMENT].Texture,
                dt,
                picture,
                Nothing,
                bindOutput,
                size);
        }
        else
        {
            Effects.Blit(picture, bindOutput);
        }

        // An effect that found nothing to draw leaves the layer bound
        Renderer2D.BindOutput(target);
        target = null;

        RenderState.Restore(before);
    }

    private unsafe void Clear()
    {
        var gl = GameEngine.Instance.GL;
        uint handle = frameBuffer!.Handle;

        Vector4 empty = Empty, still = Still;
        gl.ClearNamedFramebuffer(handle, BufferKind.Color, 0, (float*)&empty);
        gl.ClearNamedFramebuffer(handle, BufferKind.Color, MOTION_OUTPUT, (float*)&still);
    }

    /// <summary>
    /// Helper method to have the layer be as big as what it is laid over, which it is made anew for whenever that changes.
    /// </summary>
    /// <returns>False if there is no layer to be had, which has been logged.</returns>
    private bool Fit(Vector2 wanted)
    {
        uint width = (uint)MathF.Max(1.0f, wanted.X), height = (uint)MathF.Max(1.0f, wanted.Y);
        if (frameBuffer is not null && frameBuffer.Width == width && frameBuffer.Height == height)
            return true;

        // Asked for once and turned down: asking again every frame only fills the log
        if (unavailable)
            return false;

        Release();

        bool created = GameEngine.Instance.ObjectManager.FrameBuffers.TryCreate(
            new FrameBufferObjectDescription
            {
                Width = width,
                Height = height,
                Attachments = new()
                {
                    // The picture is read between its pixels by the effects, what moves in it never is
                    { FramebufferAttachment.ColorAttachment0, new FrameBufferAttachmentDefinition { IsRenderBuffer = false, TextureDefinition = PostTarget.Smooth } },
                    { MOTION_ATTACHMENT, new FrameBufferAttachmentDefinition { IsRenderBuffer = false, TextureDefinition = PostTarget.Exact } },
                }
            },
            out var result);

        if (!created)
        {
            Log.Error($"A layer of {width} by {height} couldn't be made, what was to go onto it is drawn without its effects: {result.Message}");

            unavailable = true;
            return false;
        }

        frameBuffer = result.Asset;
        size = new Vector2(width, height);
        hasPicture = false;
        return true;
    }

    private void Release()
    {
        frameBuffer?.Dispose();
        frameBuffer = null;
    }

    public void Dispose()
    {
        Effects.Dispose();
        Release();
    }
}
