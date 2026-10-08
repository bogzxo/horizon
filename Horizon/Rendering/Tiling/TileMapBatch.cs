using Horizon.Logging;
using System.Numerics;
using System.Runtime.InteropServices;

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
internal struct TileInstance : IVertex
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

    // Attributes 2 to 5, after the two of the quad's corners
    private static readonly VertexLayoutDescription[] Layout =
    [
        VertexLayoutDescription.Float(2, 2, 0, instanced: true),
        VertexLayoutDescription.Float(3, 2, sizeof(float) * 2, instanced: true),
        VertexLayoutDescription.Float(4, 4, sizeof(float) * 4, instanced: true),
        VertexLayoutDescription.Float(5, 2, sizeof(float) * 8, instanced: true)
    ];

    public static ReadOnlySpan<VertexLayoutDescription> GetLayout() => Layout;
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
/// the same tile set. It is the <see cref="UnitQuad"/> drawn as many times as there are tiles, each with what a
/// <see cref="TileInstance"/> says, out of a buffer of instances of its own.
/// </summary>
internal sealed class TileMapBatch(string imagePath)
{
    private const string UNIFORM_HAS_NORMAL = "uHasNormal";
    private const string UNIFORM_HAS_SPECULAR = "uHasSpecular";
    private const string UNIFORM_TEXEL_SIZE = "uTexelSize";

    /// <summary>The binding the instances are read through, after the quad's corners on binding 0.</summary>
    private const uint INSTANCE_BINDING = 1;

    public string ImagePath { get; } = imagePath;

    public List<TileInstance> Instances { get; } = [];

    /// <summary>The box around everything in the batch, before whatever its layer is moved by.</summary>
    public Vector2 Min { get; private set; } = new(float.MaxValue);

    /// <inheritdoc cref="Min"/>
    public Vector2 Max { get; private set; } = new(float.MinValue);

    /// <summary>Whether the instances have changed since they were last handed to the GPU.</summary>
    public bool NeedsUpload { get; set; } = true;

    // The quad with this batch's instances laid over binding 1
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
                buffers!.InstanceBuffer!.Upload<TileInstance>(CollectionsMarshal.AsSpan(Instances));
        }

        if (uploaded == 0)
            return;

        // The image, its normal map and its specular map on the units the shader says, in one call
        ReadOnlySpan<uint> handles = [texture.Albedo.Handle, texture.Normal.Handle, texture.Specular.Handle];
        Technique.BindTextures(handles);

        // An image that came without a normal or a specular map has nothing to say about its surface
        shader.SetUniform(UNIFORM_HAS_NORMAL, texture.Normal.Handle != 0);
        shader.SetUniform(UNIFORM_HAS_SPECULAR, texture.Specular.Handle != 0);

        Vector2 texel = Vector2.One / Vector2.Max(Vector2.One, texture.Size);
        shader.SetUniform(UNIFORM_TEXEL_SIZE, in texel);

        buffers!.Bind();
        Horizon.Graphics.GraphicsDevice.Current.DrawIndexedInstanced(Horizon.Graphics.Topology.Triangles, UnitQuad.INDICES, uploaded);
    }

    private bool EnsureBuffers()
    {
        if (buffers is not null)
            return true;

        try
        {
            buffers = VertexBufferObject.CreateInstanced();
        }
        catch (InvalidOperationException e)
        {
            Log.Error(e.Message);
            return false;
        }

        // A quad of one by one around its middle. The image has its first row at the top, the world has Y going up
        buffers.SetLayout<Spriting.Data.Vertex2D>();
        buffers.VertexBuffer.Upload<Spriting.Data.Vertex2D>(
        [
            new(-0.5f, -0.5f, 0.0f, 1.0f),
            new(0.5f, -0.5f, 1.0f, 1.0f),
            new(0.5f, 0.5f, 1.0f, 0.0f),
            new(-0.5f, 0.5f, 0.0f, 0.0f)
        ], BufferUsageARB.StaticDraw);
        buffers.ElementBuffer.Upload<uint>([0, 1, 2, 0, 2, 3], BufferUsageARB.StaticDraw);

        // What is different for every tile, read once per instance
        buffers.SetInstanceLayout<TileInstance>();
        return true;
    }
}
