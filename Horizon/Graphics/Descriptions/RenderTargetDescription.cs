using Horizon.Content.Descriptions;

namespace Horizon.Graphics;

/// <summary>
/// A render target, its size and what is attached at every point. Every attachment is a texture of its own, made
/// along with the target and freed with it.
/// </summary>
public readonly struct RenderTargetDescription : IAssetDescription
{
    public readonly uint Width { get; init; }
    public readonly uint Height { get; init; }
    public readonly Dictionary<AttachmentPoint, TextureDefinition> Attachments { get; init; }

    /// <summary>A target with one colour attachment and nothing else.</summary>
    public static RenderTargetDescription Color(uint width, uint height, TextureDefinition? definition = null) => new()
    {
        Width = width,
        Height = height,
        Attachments = new() { { AttachmentPoint.Color0, definition ?? TextureDefinition.RgbaUnsignedByte } }
    };
}
