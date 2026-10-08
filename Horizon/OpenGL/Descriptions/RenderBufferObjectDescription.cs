using Horizon.Content.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Descriptions;

public readonly struct RenderBufferObjectDescription : IAssetDescription
{
    public readonly uint Width { get; init; }
    public readonly uint Height { get; init; }
    public readonly uint Samples { get; init; }
    public readonly InternalFormat InternalFormat { get; init; }

    public static RenderBufferObjectDescription RGBA { get; } = new()
    {
        InternalFormat = InternalFormat.Rgba
    };
    public static RenderBufferObjectDescription DepthComponent { get; } = new()
    {
        InternalFormat = InternalFormat.DepthComponent
    };
}
