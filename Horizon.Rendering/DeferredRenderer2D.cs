using Horizon.Engine;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Rendering;

/// <summary>
/// Implementation of <see cref="Renderer2D"/> with deferred lighting support with normal and specular mapping.
/// Attachment0 contains the albedo texture and attachment1 contains the normal in the RG channels and the fragment position in the BA channels.
/// </summary>
public class DeferredRenderer2D(in uint width, in uint height) : Renderer2D(width, height)
{
    protected override FrameBufferObject CreateFrameBuffer(in uint width, in uint height) => GameEngine
            .Instance
            .ObjectManager
            .FrameBuffers
            .Create(
                new FrameBufferObjectDescription
                {
                    Width = width,
                    Height = height,
                    Attachments = new() {
                        { FramebufferAttachment.ColorAttachment0, TextureDefinition.RgbaUnsignedByte },
                        { FramebufferAttachment.ColorAttachment1, TextureDefinition.RgbaUnsignedByte },
                        { FramebufferAttachment.ColorAttachment2, new() {
                            InternalFormat = InternalFormat.R32ui,
                            PixelFormat = PixelFormat.RedInteger,
                            PixelType = PixelType.UnsignedInt,
                            TextureTarget = TextureTarget.Texture2D
                        } },
                    },
                }
            )
            .Asset;

    protected override Renderer2DTechnique CreateTechnique() => new DeferredRenderer2DTechnique(FrameBuffer);
}