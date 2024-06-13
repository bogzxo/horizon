using Horizon.OpenGL.Assets;

namespace Horizon.OpenGL.Descriptions;

public readonly struct FrameBufferAttachmentAsset
{
    public FrameBufferAttachmentType Type { get; init; }
    public Texture Texture { get; init; }
    public RenderBufferObject RenderBuffer { get; init; }
}
