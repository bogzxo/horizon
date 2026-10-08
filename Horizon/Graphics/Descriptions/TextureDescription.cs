using Horizon.Content.Descriptions;

namespace Horizon.Graphics;

/// <summary>
/// What a texture is made from, a file to read (the size comes from the file), or a size and nothing in it yet.
/// </summary>
public readonly struct TextureDescription : IAssetDescription
{
    /// <summary>The file to read, if any.</summary>
    public readonly string[] Paths { get; init; }

    public readonly uint Width { get; init; }
    public readonly uint Height { get; init; }
    public readonly TextureDefinition Definition { get; init; }

    public TextureDescription()
    {
        Paths = [];
        Definition = TextureDefinition.RgbaUnsignedByte;
    }
}
