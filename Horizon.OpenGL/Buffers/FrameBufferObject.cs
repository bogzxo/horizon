using System.Diagnostics;

using Horizon.Core.Primitives;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.OpenGL.Buffers;

public class FrameBufferObject : IGLObject
{
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
        //ObjectManager.GL.DrawBuffers((uint)DrawBuffers.Length, in DrawBuffers[0]);
        ObjectManager
            .GL
            .NamedFramebufferDrawBuffers(Handle, DrawBuffers);
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