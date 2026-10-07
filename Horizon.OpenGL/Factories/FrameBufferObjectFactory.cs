using Bogz.Logging;
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

        // Only the colour attachments can be drawn to: handing over a depth or stencil attachment as well has the
        // whole call rejected, which leaves the frame buffer drawing to its first colour attachment alone.
        // They go by their number, so that output N of a fragment shader ends up in colour attachment N whether or
        // not the ones before it are there: an output there is no attachment for is thrown away.
        int[] colours = [.. attachments.Keys.Where(IsColorAttachment).Select(attachment => attachment - FramebufferAttachment.ColorAttachment0)];

        var drawBuffers = new ColorBuffer[colours.Length == 0 ? 0 : colours.Max() + 1];
        Array.Fill(drawBuffers, ColorBuffer.None);

        foreach (int colour in colours)
            drawBuffers[colour] = (ColorBuffer)(FramebufferAttachment.ColorAttachment0 + colour);

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

        if (drawBuffers.Length == 0)
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


    private static bool IsColorAttachment(FramebufferAttachment attachment) =>
        attachment >= FramebufferAttachment.ColorAttachment0 && attachment <= FramebufferAttachment.ColorAttachment31;

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
                    Log.Error(renderBuffer.Message);
                }

                if (renderBuffer.Status == AssetCreationStatus.Failed)
                {
                    Log.Error($"[FrameBufferFactory] Failed to create attachment render buffer: {renderBuffer.Message}");
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
                    Log.Error(texture.Message);
                }

                if (texture.Status == AssetCreationStatus.Failed)
                {
                    Log.Error($"[FrameBufferFactory] Failed to create attachment texture: {texture.Message}");
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