namespace Horizon.OpenGL.Descriptions;

public readonly struct FrameBufferAttachmentDefinition
{
    public readonly bool IsRenderBuffer { get; init; }
    public readonly TextureDefinition TextureDefinition { get; init; }
    public readonly RenderBufferObjectDescription RenderBufferDescription { get; init; }

    public static FrameBufferAttachmentDefinition TextureRGBAF { get; } = new() { 
        IsRenderBuffer = false,
        TextureDefinition = TextureDefinition.RgbaFloat,
    };

    public static FrameBufferAttachmentDefinition RenderBufferRGBA { get; } = new()
    {
        IsRenderBuffer = true,
        RenderBufferDescription = RenderBufferObjectDescription.RGBA
    };

    public static FrameBufferAttachmentDefinition TextureRGBAByte { get; } = new()
    {
        IsRenderBuffer = false,
        TextureDefinition = TextureDefinition.RgbaUnsignedByte,
    };

    public static FrameBufferAttachmentDefinition TextureDepth { get; } = new()
    {
        IsRenderBuffer = false,
        TextureDefinition = TextureDefinition.DepthComponent,
    };

    public static FrameBufferAttachmentDefinition RenderBufferDepth { get; } = new()
    {
        IsRenderBuffer = true,
        RenderBufferDescription = RenderBufferObjectDescription.DepthComponent,
    };
}
