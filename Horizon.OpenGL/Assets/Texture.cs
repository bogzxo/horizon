using System.Numerics;

using Horizon.Core.Primitives;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

namespace Horizon.OpenGL.Assets;

/// <summary>
/// A picture on the GPU. Loading one is a single line, and it is freed along with the scene that asked for it.
/// <code>
/// Texture backdrop = Texture.Load("Assets/backgrounds/dojo.png");
/// </code>
/// </summary>
public class Texture : IGLObject, IDisposable
{
    // What tells a smoothed copy of a file apart from the crisp one in the cache
    private const string SMOOTH_SUFFIX = "|smooth";

    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint Handle { get; init; }
    public TextureTarget TextureTarget { get; init; }

    public Vector2 Size => new(Width, Height);

    /// <summary>
    /// Whether there is a picture here at all. What failed to load is not valid, and draws as nothing.
    /// </summary>
    public bool IsValid => Handle != 0;

    public static Texture Invalid { get; } =
        new Texture
        {
            Handle = 0,
            Width = 0,
            Height = 0,
            TextureTarget = TextureTarget.Texture2D,
        };

    /// <summary>
    /// Loads a picture from a file. Asking for the same file again hands back the same texture, it is only read once.
    /// </summary>
    /// <param name="smooth">Whether it is smoothed when it is drawn bigger or smaller than it is. Pixel art wants this off.</param>
    /// <returns>The texture, or <see cref="Invalid"/> if the file is fucked or missing (which is logged).</returns>
    public static Texture Load(string path, bool smooth = false) =>
        Load(
            smooth ? path + SMOOTH_SUFFIX : path,
            path,
            smooth ? TextureDefinition.RgbaUnsignedByte : TextureDefinition.RgbaUnsignedByteNearest);

    /// <summary>
    /// Loads a picture from a file the way a definition says, and keeps it under a name of its own.
    /// </summary>
    public static Texture Load(string name, string path, TextureDefinition definition) =>
        ObjectManager.Instance.Textures.CreateOrGet(name, new TextureDescription { Paths = [path], Definition = definition }) ?? Invalid;

    /// <summary>
    /// Makes an empty texture to draw into or fill in later. It belongs to whoever made it, see <see cref="Dispose"/>.
    /// </summary>
    public static Texture Create(uint width, uint height, TextureDefinition? definition = null) =>
        ObjectManager.Instance.Textures.Create(new TextureDescription
        {
            Width = width,
            Height = height,
            Definition = definition ?? TextureDefinition.RgbaUnsignedByteNearest
        }) ?? Invalid;

    public void Bind(uint bindingPoint)
    {
        ObjectManager
            .GL
            .BindTextureUnit(bindingPoint, Handle);
    }

    /// <summary>
    /// Frees the texture right now, for whoever doesn't want to wait for the scene to end. Render thread.
    /// </summary>
    public void Dispose()
    {
        if (IsValid) ObjectManager.Instance.Textures.Remove(Handle);

        GC.SuppressFinalize(this);
    }
}
