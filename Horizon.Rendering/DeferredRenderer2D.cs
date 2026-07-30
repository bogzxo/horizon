using Horizon.Engine;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

/// <summary>
/// Implementation of <see cref="Renderer2D"/> with deferred lighting support with normal and specular mapping.
/// Attachment0 contains the albedo texture and attachment1 contains the normal in the RG channels and the fragment position in the BA channels.
/// </summary>
public class DeferredRenderer2D : Renderer2D
{
    protected override FrameBufferObject CreateFrameBuffer(in uint width, in uint height)
    {
        if (GameEngine
            .Instance
            .ObjectManager
            .FrameBuffers
            .TryCreate(
                new FrameBufferObjectDescription
                {
                    Width = width,
                    Height = height,
                    Attachments = new() {
                    { FramebufferAttachment.ColorAttachment0, FrameBufferAttachmentDefinition.TextureRGBAByte },
                    { FramebufferAttachment.ColorAttachment1, FrameBufferAttachmentDefinition.TextureRGBAByte },
                    }
                },
                out var result
            ))
        {
            return result.Asset;
        }
        else
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
            throw new Exception(result.Message);
        }
    }

    protected override Renderer2DTechnique CreateTechnique() => new DeferredRenderer2DTechnique(FrameBuffer);

    public DeferredRenderer2D(in uint width, in uint height)
        : base(width, height) { }
}