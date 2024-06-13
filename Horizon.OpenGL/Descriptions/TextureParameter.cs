using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Descriptions;

public readonly struct TextureParameter
{
    public readonly TextureParameterName Name { get; init; }
    public readonly int Value { get; init; }
}
