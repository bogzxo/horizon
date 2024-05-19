using System.Diagnostics.Contracts;

using Horizon.Content;
using Horizon.Content.Descriptions;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Factories;

public class FrameBufferObjectFactory
    : IAssetFactory<FrameBufferObject, FrameBufferObjectDescription>
{
    public static unsafe AssetCreationResult<FrameBufferObject> Create(
    in FrameBufferObjectDescription description
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

        foreach (var (attachment, texture) in attachments)
        {
            // Ensure correct attachment point for each texture
            ObjectManager.GL.NamedFramebufferTexture(
                buffer.Handle,
                (FramebufferAttachment)attachment, // Cast attachment to correct enum type
                texture.Handle,
                0
            );
        }

        // Check if the framebuffer is complete
        var status = ObjectManager.GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            // Unbind the framebuffer
            ObjectManager.GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            // Cleanup
            ObjectManager.GL.DeleteFramebuffer(buffer.Handle);
            foreach (var (_, texture) in attachments)
                ObjectManager.Instance.Textures.Remove(texture);

            return new AssetCreationResult<FrameBufferObject>
            {
                Asset = buffer,
                Message = $"Framebuffer is incomplete: {status}",
                Status = AssetCreationStatus.Failed
            };
        }

        // Unbind the framebuffer
        ObjectManager.GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return new AssetCreationResult<FrameBufferObject>
        {
            Asset = buffer,
            Status = AssetCreationStatus.Success
        };
    }


    private static Dictionary<FramebufferAttachment, Assets.Texture> CreateFrameBufferAttachments(
        uint width,
        uint height,
        Dictionary<FramebufferAttachment, TextureDefinition> attachmentTypes
    )
    {
        var attachments = new Dictionary<FramebufferAttachment, Assets.Texture>();

        foreach (var (attachmentType, definition) in attachmentTypes)
        {
            var texture = ObjectManager
                    .Instance
                    .Textures
                    .Create(new()
                    {
                        Definition = definition,
                        Height = height,
                        Width = width
                    });

            if (texture.Status == AssetCreationStatus.Failed)
            {
                Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, $"[FrameBufferFactory] Failed to create attachment texture: {texture.Message}");
            }

            attachments.Add(
                attachmentType,
                texture.Asset
            );
        }

        return attachments;
    }
}