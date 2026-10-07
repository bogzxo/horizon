using System.Diagnostics;

using Horizon.Core.Primitives;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.OpenGL.Buffers;

public class FrameBufferObject : IGLObject, IDisposable
{
    /// <summary>
    /// Makes a frame buffer with everything a description says is attached to it. Throws if the GPU won't have it.
    /// </summary>
    public static FrameBufferObject Create(in FrameBufferObjectDescription description) =>
        ObjectManager.Instance.FrameBuffers.TryCreate(description, out var result)
            ? result.Asset
            : throw new InvalidOperationException(result.Message);

    /// <summary>
    /// The texture behind one of the attachments.
    /// </summary>
    public Texture TextureOf(FramebufferAttachment attachment) => Attachments[attachment].Texture;

    /// <summary>
    /// The first colour attachment, which is the picture for most frame buffers.
    /// </summary>
    public Texture Color => Attachments[FramebufferAttachment.ColorAttachment0].Texture;

    /// <summary>
    /// Frees the frame buffer and everything that is attached to it. Render thread.
    /// </summary>
    public void Dispose()
    {
        var manager = ObjectManager.Instance;

        // A frame buffer doesn't own what is attached to it as far as the GPU cares, those go one by one
        foreach (var (_, attachment) in Attachments)
        {
            if (attachment.Type == FrameBufferAttachmentType.Texture) manager.Textures.Remove(attachment.Texture);
            else manager.RenderBuffers.Remove(attachment.RenderBuffer);
        }

        manager.FrameBuffers.Remove(this);
        GC.SuppressFinalize(this);
    }

    public Dictionary<FramebufferAttachment, FrameBufferAttachmentAsset> Attachments { get; init; }
    public ColorBuffer[] DrawBuffers { get; init; }

    /// <summary>
    /// Binds a specified attachment to a texture unit.
    /// </summary>
    public void BindAttachment(in FramebufferAttachment type, in uint index)
    {
#if DEBUG
        Debug.Assert(Attachments[type].Texture.Handle != 0);
#endif

        Attachments[type].Texture.Bind(index);
    }
        


    /// <summary>
    /// Binds the current frame buffer and binds its buffers to be draw to.
    /// </summary>
    public void Bind()
    {
        ObjectManager.GL.BindFramebuffer(FramebufferTarget.Framebuffer, Handle);

        // A frame buffer with only depth has nothing to list, it was told to draw to nothing when it was made
        if (DrawBuffers.Length > 0)
        {
            ObjectManager
                .GL
                .NamedFramebufferDrawBuffers(Handle, DrawBuffers);
        }
    }

    /// <summary>
    /// Sets viewport size to the size of the frame buffer.
    /// </summary>
    public void Viewport() => ObjectManager.GL.Viewport(0, 0, Width, Height);

    public static void Unbind() =>
        ObjectManager.GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

    public uint Handle { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
}