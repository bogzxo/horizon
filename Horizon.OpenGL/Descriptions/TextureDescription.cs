using Horizon.Content.Descriptions;

namespace Horizon.OpenGL.Descriptions;

/// <summary>
/// A struct representing the arguments needed to create a valid texture.
/// </summary>
public readonly struct TextureDescription : IAssetDescription
{
    public readonly string[] Paths { get; init; }
    public readonly uint Width { get; init; }
    public readonly uint Height { get; init; }
    public readonly TextureDefinition Definition { get; init; }

    public TextureDescription()
    {
        Paths = Array.Empty<string>();
        Definition = TextureDefinition.RgbaFloat;
    }
}