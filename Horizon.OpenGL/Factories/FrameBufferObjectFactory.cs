using System.Diagnostics.Contracts;

using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.Core.Native;
using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Factories;

public class FrameBufferObjectFactory
    : IAssetFactory<FrameBufferObject, FrameBufferObjectDescription>
{
    public static unsafe bool TryCreate(
    in FrameBufferObjectDescription description,
    out AssetCreationResult<FrameBufferObject> asset
)
    {
        // Delegates texture creation to the texture manager.
        var attachments = CreateFrameBufferAttachments(
            description.Width,
            description.Height,
            description.Attachments
        );

        var drawBuffers = attachments.Select(x => (ColorBuffer)x.Key).ToArray();

        var buffer = new FrameBufferObject
        {
            Handle = ObjectManager.GL.CreateFramebuffer(),
            Width = description.Width,
            Height = description.Height,
            Attachments = attachments,
            DrawBuffers = drawBuffers
        };

        ObjectManager.GL.BindFramebuffer(FramebufferTarget.Framebuffer, buffer.Handle);

        foreach (var (attachmentType, attachment) in attachments)
        {
            if (attachment.Type == FrameBufferAttachmentType.Texture)
            {
                // Ensure correct attachment point for each texture
                ObjectManager.GL.NamedFramebufferTexture(
                    buffer.Handle,
                    attachmentType,
                    attachment.Texture.Handle,
                    0
                );
            }
            else
            {
                ObjectManager.GL.NamedFramebufferRenderbuffer(buffer.Handle, attachmentType, RenderbufferTarget.Renderbuffer, attachment.RenderBuffer.Handle);
            }
        }

        if (attachments.Count == 1 && attachments.ContainsKey(FramebufferAttachment.DepthAttachment))
        {
            ObjectManager.GL.DrawBuffer(DrawBufferMode.None);
            ObjectManager.GL.ReadBuffer(ReadBufferMode.None);
        }

        // Check if the framebuffer is complete
        var status = ObjectManager.GL.CheckNamedFramebufferStatus(buffer.Handle, FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            // Unbind the framebuffer
            ObjectManager.GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            // Cleanup
            ObjectManager.GL.DeleteFramebuffer(buffer.Handle);

            foreach (var (_, attachment) in attachments)
                if (attachment.Type == FrameBufferAttachmentType.Texture)
                    ObjectManager.Instance.Textures.Remove(attachment.Texture);
                else ObjectManager.Instance.RenderBuffers.Remove(attachment.RenderBuffer);

            asset = new AssetCreationResult<FrameBufferObject>
            {
                Asset = buffer,
                Message = $"Framebuffer is incomplete: {status}",
                Status = AssetCreationStatus.Failed
            };
            return false;
        }

        // Unbind the framebuffer
        ObjectManager.GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        asset = new AssetCreationResult<FrameBufferObject>
        {
            Asset = buffer,
            Status = AssetCreationStatus.Success
        };
        return true;
    }


    private static Dictionary<FramebufferAttachment, FrameBufferAttachmentAsset> CreateFrameBufferAttachments(
        uint width,
        uint height,
        Dictionary<FramebufferAttachment, FrameBufferAttachmentDefinition> attachmentTypes
    )
    {
        var attachments = new Dictionary<FramebufferAttachment, FrameBufferAttachmentAsset>();

        foreach (var (attachmentType, definition) in attachmentTypes)
        {
            if (definition.IsRenderBuffer)
            {
                if (!ObjectManager.Instance.RenderBuffers.TryCreate(
                    definition.RenderBufferDescription with
                    {
                        Width = width,
                        Height = height,
                    },
                    out var renderBuffer))
                {
                    Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, renderBuffer.Message);
                }

                if (renderBuffer.Status == AssetCreationStatus.Failed)
                {
                    Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, $"[FrameBufferFactory] Failed to create attachment render buffer: {renderBuffer.Message}");
                }

                attachments.Add(
                    attachmentType,
                    new FrameBufferAttachmentAsset
                    {
                        RenderBuffer = renderBuffer.Asset,
                        Type = FrameBufferAttachmentType.RenderBuffer
                    }
                );
            }
            else
            {
             if(!ObjectManager
                   .Instance
                   .Textures
                   .TryCreate(new()
                   {
                       Definition = definition.TextureDefinition,
                       Height = height,
                       Width = width
                   }, out var texture))
                {
                    Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, texture.Message);
                }

                if (texture.Status == AssetCreationStatus.Failed)
                {
                    Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, $"[FrameBufferFactory] Failed to create attachment texture: {texture.Message}");
                }

                attachments.Add(
                    attachmentType,
                    new FrameBufferAttachmentAsset
                    {
                        Texture = texture.Asset,
                        Type = FrameBufferAttachmentType.Texture
                    }
                );
            }
        }

        return attachments;
    }
}