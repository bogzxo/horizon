using Horizon.Core.Primitives;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Assets;
public class Texture : IGLObject
{
    public uint Width { get; init; }
    public uint Height { get; init; }

    public uint Handle { get; init; }
    public TextureTarget TextureTarget { get; init; }

    public static Texture Invalid { get; } =
        new Texture
        {
            Handle = 0,
            Width = 0,
            Height = 0,
            TextureTarget = TextureTarget.Texture2D,
        };

    public void Bind(uint bindingPoint)
    {
        ObjectManager
            .GL
            .BindTextureUnit(bindingPoint, Handle);
    }
}