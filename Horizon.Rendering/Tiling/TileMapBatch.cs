using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Core.Data;
using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.OpenGL.Managers;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Rendering.Tiling;

/// <summary>
/// One tile (or image) the way the shader is handed it, see shaders/tilemap/tilemap.vert.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TileInstance
{
    // The middle of the quad, before whatever its layer is moved by
    public Vector2 Position;
    public Vector2 Size;

    // Where in the image it is cut out of, in pixels from the top left corner: left, top, right, bottom
    public Vector4 Source;

    // In radians, counter-clockwise around the middle
    public float Rotation;

    // A TileFlip
    public float Flip;

    public const uint SizeInBytes = sizeof(float) * 10;
}

/// <summary>A tile of a layer that plays through frames: which instance it is, and the frame it is showing.</summary>
internal sealed class TileMapAnimated(TileMapBatch batch, int index, TileMapTile tile)
{
    public TileMapBatch Batch { get; } = batch;
    public int Index { get; } = index;
    public TileMapTile Tile { get; } = tile;
    public int Frame { get; set; } = -1;
}

/// <summary>
/// The images a map draws with. An image can come with a normal map and a specular map, which are the files next to
/// it that are called the same with <c>_normal</c> and <c>_specular</c> at the end (in place of <c>_albedo</c>, for
/// an image that has that at the end of its own name).
/// </summary>
internal sealed class TileMapTexture
{
    private const string ALBEDO_SUFFIX = "_albedo";

    public Texture Albedo { get; private init; } = Texture.Invalid;
    public Texture Normal { get; private init; } = Texture.Invalid;
    public Texture Specular { get; private init; } = Texture.Invalid;

    public Vector2 Size => new(Albedo.Width, Albedo.Height);

    /// <summary>Has to run on the GL thread. An image that isn't there gives a texture that draws nothing.</summary>
    public static TileMapTexture Load(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        if (name.EndsWith(ALBEDO_SUFFIX, StringComparison.OrdinalIgnoreCase))
            name = name[..^ALBEDO_SUFFIX.Length];

        return new TileMapTexture
        {
            Albedo = LoadImage(path),
            Normal = LoadImage(Path.Combine(directory, $"{name}_normal{extension}")),
            Specular = LoadImage(Path.Combine(directory, $"{name}_specular{extension}"))
        };
    }

    private static Texture LoadImage(string path)
    {
        if (!File.Exists(path))
            return Texture.Invalid;

        // By where it is, so two maps that use the same image share it
        bool loaded = GameEngine.Instance.ObjectManager.Textures.TryCreateOrGet(
            Path.GetFullPath(path),
            new TextureDescription { Paths = [path], Definition = TextureDefinition.RgbaUnsignedByteNearest },
            out var result);

        return loaded ? result.Asset : Texture.Invalid;
    }
}

/// <summary>
/// Everything of a layer that is drawn with one image in one go: the tiles of a part of the layer that are out of
/// the same tile set. It is a quad drawn as many times as there are tiles, each with what a <see cref="TileInstance"/> says.
/// </summary>
internal sealed class TileMapBatch(string imagePath)
{
    private const string UNIFORM_TEXTURE_ALBEDO = "uTextureAlbedo";
    private const string UNIFORM_TEXTURE_NORMAL = "uTextureNormal";
    private const string UNIFORM_TEXTURE_SPECULAR = "uTextureSpecular";
    private const string UNIFORM_HAS_NORMAL = "uHasNormal";
    private const string UNIFORM_HAS_SPECULAR = "uHasSpecular";
    private const string UNIFORM_TEXEL_SIZE = "uTexelSize";

    // A corner of the quad and which corner of the tile's image goes there (the top left one being 0, 0)
    private readonly struct Corner(float x, float y, float u, float v) : IVertex
    {
        public readonly Vector2 Position = new(x, y);
        public readonly Vector2 TexCoords = new(u, v);

        public static uint SizeInBytes { get; } = sizeof(float) * 4;

        public static ReadOnlySpan<VertexLayoutDescription> GetLayout() => new VertexLayoutDescription[]
        {
            new() { Index = 0, Size = sizeof(float) * 2, Count = 2, Offset = 0, Type = VertexAttribPointerType.Float, Instanced = false },
            new() { Index = 1, Size = sizeof(float) * 2, Count = 2, Offset = sizeof(float) * 2, Type = VertexAttribPointerType.Float, Instanced = false }
        };
    }

    public string ImagePath { get; } = imagePath;

    public List<TileInstance> Instances { get; } = [];

    /// <summary>The box around everything in the batch, before whatever its layer is moved by.</summary>
    public Vector2 Min { get; private set; } = new(float.MaxValue);

    /// <inheritdoc cref="Min"/>
    public Vector2 Max { get; private set; } = new(float.MinValue);

    /// <summary>Whether the instances have changed since they were last handed to the GPU.</summary>
    public bool NeedsUpload { get; set; } = true;

    private VertexBufferObject? buffers;
    private uint uploaded;

    public int Add(in TileInstance instance)
    {
        // Turned any way it still fits in a circle around its middle
        float reach = instance.Rotation == 0.0f ? MathF.Max(instance.Size.X, instance.Size.Y) / 2.0f : instance.Size.Length() / 2.0f;

        Min = Vector2.Min(Min, instance.Position - new Vector2(reach));
        Max = Vector2.Max(Max, instance.Position + new Vector2(reach));

        Instances.Add(instance);
        NeedsUpload = true;
        return Instances.Count - 1;
    }

    public void Clear()
    {
        Instances.Clear();
        Min = new Vector2(float.MaxValue);
        Max = new Vector2(float.MinValue);
        NeedsUpload = true;
    }

    /// <summary>Draws the batch with a shader that is bound and has everything set that is the same for the whole layer.</summary>
    public unsafe void Draw(Technique shader, TileMapTexture texture)
    {
        if (texture.Albedo.Handle == 0 || !EnsureBuffers())
            return;

        if (NeedsUpload)
        {
            NeedsUpload = false;
            uploaded = (uint)Instances.Count;

            if (uploaded > 0)
                buffers!.InstanceBuffer!.NamedBufferData<TileInstance>(CollectionsMarshal.AsSpan(Instances));
        }

        if (uploaded == 0)
            return;

        ObjectManager.GL.BindTextureUnit(0, texture.Albedo.Handle);
        ObjectManager.GL.BindTextureUnit(1, texture.Normal.Handle);
        ObjectManager.GL.BindTextureUnit(2, texture.Specular.Handle);

        shader.SetUniform(UNIFORM_TEXTURE_ALBEDO, 0);
        shader.SetUniform(UNIFORM_TEXTURE_NORMAL, 1);
        shader.SetUniform(UNIFORM_TEXTURE_SPECULAR, 2);

        // An image that came without a normal or a specular map has nothing to say about its surface
        shader.SetUniform(UNIFORM_HAS_NORMAL, texture.Normal.Handle != 0);
        shader.SetUniform(UNIFORM_HAS_SPECULAR, texture.Specular.Handle != 0);

        Vector2 texel = Vector2.One / Vector2.Max(Vector2.One, texture.Size);
        shader.SetUniform(UNIFORM_TEXEL_SIZE, in texel);

        buffers!.Bind();
        buffers.VertexBuffer.Bind();
        buffers.ElementBuffer.Bind();

        GameEngine.Instance.GL.DrawElementsInstanced(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, null, uploaded);

        buffers.Unbind();
    }

    private bool EnsureBuffers()
    {
        if (buffers is not null)
            return true;

        bool created = GameEngine.Instance.ObjectManager.VertexArrays.TryCreate(
            new VertexArrayObjectDescription
            {
                Buffers = new()
                {
                    { VertexArrayBufferAttachmentType.ArrayBuffer, BufferObjectDescription.ArrayBuffer },
                    { VertexArrayBufferAttachmentType.ElementBuffer, BufferObjectDescription.ElementArrayBuffer },
                    { VertexArrayBufferAttachmentType.AdditionalBuffer0, BufferObjectDescription.ArrayBuffer }
                }
            },
            out var result);

        if (!created)
        {
            Bogz.Logging.Loggers.ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, result.Message);
            return false;
        }

        buffers = new VertexBufferObject(result.Asset);

        // A quad of one by one around its middle. The image has its first row at the top, the world has Y going up
        buffers.VertexBuffer.BufferData(new Corner[]
        {
            new(-0.5f, -0.5f, 0.0f, 1.0f),
            new(0.5f, -0.5f, 1.0f, 1.0f),
            new(0.5f, 0.5f, 1.0f, 0.0f),
            new(-0.5f, 0.5f, 0.0f, 0.0f)
        });
        buffers.ElementBuffer.BufferData(new uint[] { 0, 1, 2, 0, 2, 3 });

        buffers.Bind();
        buffers.VertexBuffer.Bind();
        buffers.VertexBuffer.SetLayout<Corner>();

        // What is different for every tile, read once per instance
        buffers.InstanceBuffer!.Bind();
        buffers.VertexBuffer.VertexAttributePointer(2, 2, VertexAttribPointerType.Float, TileInstance.SizeInBytes, 0);
        buffers.VertexBuffer.VertexAttributePointer(3, 2, VertexAttribPointerType.Float, TileInstance.SizeInBytes, 2 * sizeof(float));
        buffers.VertexBuffer.VertexAttributePointer(4, 4, VertexAttribPointerType.Float, TileInstance.SizeInBytes, 4 * sizeof(float));
        buffers.VertexBuffer.VertexAttributePointer(5, 2, VertexAttribPointerType.Float, TileInstance.SizeInBytes, 8 * sizeof(float));

        for (uint attribute = 2; attribute <= 5; attribute++)
            buffers.VertexBuffer.VertexAttributeDivisor(attribute, 1);

        buffers.Unbind();
        buffers.VertexBuffer.Unbind();
        return true;
    }
}
