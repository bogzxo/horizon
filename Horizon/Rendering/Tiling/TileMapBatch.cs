using Horizon.Logging;
using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Tiling;

/// <summary>
/// One tile (or image) as the map keeps it on the CPU, before the layer it is on and its textures are added for the
/// GPU, see <see cref="TileMapGpu.Tile"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TileInstance
{
    // The middle of the quad, before whatever its layer is moved by
    public Vector2 Position;
    public Vector2 Size;

    // Where in the image it is cut out of, in pixels from the top left corner, left, top, right, bottom
    public Vector4 Source;

    // In radians, counter-clockwise around the middle
    public float Rotation;

    // A TileFlip
    public float Flip;
}

/// <summary>A tile of a layer that plays through frames, which instance it is and the frame it is showing.</summary>
internal sealed class TileMapAnimated(TileMapBatch batch, int index, TileMapTile tile)
{
    public TileMapBatch Batch { get; } = batch;
    public int Index { get; } = index;
    public TileMapTile Tile { get; } = tile;
    public int Frame { get; set; } = -1;
}

/// <summary>
/// The images a map draws with. An image can come with a normal map, a specular map and an ambient occlusion map,
/// which are the files next to it that are called the same with <c>_normal</c>, <c>_specular</c> and <c>_ao</c> at
/// the end (in place of <c>_albedo</c>, for an image that has that at the end of its own name).
/// </summary>
internal sealed class TileMapTexture
{
    private const string ALBEDO_SUFFIX = "_albedo";

    public Texture Albedo { get; private init; } = Texture.Invalid;
    public Texture Normal { get; private init; } = Texture.Invalid;
    public Texture Specular { get; private init; } = Texture.Invalid;

    /// <summary>How much of the ambient light gets to every texel, white for all of it. See DeferredRenderer2D.AmbientOcclusion.</summary>
    public Texture Occlusion { get; private init; } = Texture.Invalid;

    public Vector2 Size => new(Albedo.Width, Albedo.Height);

    /// <summary>Has to run on the render thread. An image that isn't there gives a texture that draws nothing.</summary>
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
            Specular = LoadImage(Path.Combine(directory, $"{name}_specular{extension}")),
            Occlusion = LoadImage(Path.Combine(directory, $"{name}_ao{extension}"))
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
/// Everything of a layer that is drawn with one image and sits together, the tiles of a part of the layer that are
/// out of the same tile set. On the GPU it is a chunk of the map's one tile buffer, see <see cref="TileMapGpu"/>.
/// </summary>
internal sealed class TileMapBatch(string imagePath)
{
    public string ImagePath { get; } = imagePath;

    public List<TileInstance> Instances { get; } = [];

    /// <summary>The box around everything in the batch, before whatever its layer is moved by.</summary>
    public Vector2 Min { get; private set; } = new(float.MaxValue);

    /// <inheritdoc cref="Min"/>
    public Vector2 Max { get; private set; } = new(float.MinValue);

    /// <summary>Whether the instances have changed since they were last handed to the GPU.</summary>
    public bool NeedsUpload { get; set; } = true;

    /// <summary>Where the batch's tiles are in the map's tile buffer, as of the last packing, and how many there were then.</summary>
    internal int First = -1;
    internal int Packed = -1;

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
}

/// <summary>
/// The map on the GPU. Every tile of every layer is in one storage buffer, in the chunks the batches are, the layers'
/// settings are in another, and a compute pass (shaders/tilemap/tilemap_cull.slang) writes a draw for every chunk in
/// view. Drawing the map is one indirect draw, however many layers and tile sets it has. The tiles only go up again
/// when a batch changed (a tile animating, an image layer placed anew), the rest never touches the CPU again.
/// </summary>
internal sealed class TileMapGpu : IDisposable
{
    /// <summary>Must match Layer in shaders/tilemap/tilemap.slang and tilemap_cull.slang (64 bytes, a whole number of 16 like every struct in a block).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Layer
    {
        public Vector2 Offset;

        /// <summary>Where the picture of the map's geometry occlusion starts in the world, see TileMap.GeometryOcclusion.</summary>
        public Vector2 GeometryOrigin;

        /// <summary>Its slot in the bindless table, <see cref="NoTexture"/> for a layer that doesn't read it.</summary>
        public uint Geometry;
        public float Emissive;

        /// <summary>One over the size of the map in the world, which is what the picture covers.</summary>
        public Vector2 GeometryScale;
        public Vector4 Tint;

        /// <summary>How much the occlusion maps of the tile sets count, see TileMap.OcclusionMapStrength.</summary>
        public float OcclusionMaps;
        public float Padding0, Padding1, Padding2;
    }

    /// <summary>What a slot says when there is no texture in it, NO_TEXTURE in the shaders.</summary>
    public const uint NoTexture = SpriteItemNoTexture;

    /// <summary>Must match Tile in shaders/tilemap/tilemap.slang (80 bytes, a whole number of 16, see there).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Tile
    {
        public Vector2 Position;
        public Vector2 Size;
        public Vector4 Source;
        public float Rotation;
        public float Flip;
        public uint LayerIndex;
        public uint Albedo;
        public uint Normal;
        public uint Specular;
        public Vector2 TexelSize;
        public uint Occlusion;
        public uint Padding0, Padding1, Padding2;
    }

    /// <summary>Must match Chunk in shaders/tilemap/tilemap_cull.slang (32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Chunk
    {
        public Vector2 Min;
        public Vector2 Max;
        public uint First;
        public uint Count;
        public uint LayerIndex;
        public uint Foreground;
    }

    private const uint LAYERS_BINDING = 2;
    private const uint TILES_BINDING = 3;
    private const uint CHUNKS_BINDING = 3;
    private const uint COMMANDS_BINDING = 4;
    private const uint COUNT_BINDING = 5;

    private static Technique? drawShader, cullShader;

    private GpuBuffer? layers, tiles, chunks, commands, count;
    private readonly List<Tile> packed = [];
    private readonly List<Chunk> chunkList = [];
    private readonly List<Layer> layerList = [];

    // The layers as they were last sent up, to tell whether they have to go up again
    private Layer[] uploadedLayers = [];
    private int uploadedLayerCount;

    // Where the tiles of a batch that changed are turned into what the GPU reads, kept from one time to the next
    private Tile[] patching = [];
    private int tileCapacity;
    private bool packedOnce;

    /// <summary>How many chunks were put together last, which is the most draws there can be.</summary>
    public int ChunkCount => chunkList.Count;

    /// <summary>
    /// Brings the GPU side up to date with the layers as they are to be drawn this frame. Every layer's settings go
    /// up, and the tiles of any batch that changed. A batch that grew or shrank has the whole map packed anew.
    /// </summary>
    /// <param name="shown">
    /// Every layer and how it is shown this frame, with the batches of each. A list by its own name and not by an
    /// interface, walked through an interface it makes an enumerator on the heap every time, and it is walked
    /// several times a frame. The performance overlay had that down as a hundred and fifty kilobytes a second.
    /// </param>
    public bool Sync(List<(TileMapLayer Layer, Layer Settings, bool Foreground)> shown, Func<string, TileMapTexture> textureOf)
    {
        if (!EnsureShaders()) return false;

        // The layers, every frame, they are small and they move
        layerList.Clear();
        foreach (var (_, settings, _) in shown) layerList.Add(settings);
        if (layerList.Count == 0) return false;

        if (layers is null)
        {
            layers = GpuBuffer.Create(new BufferDescription(BufferUsage.Storage, BufferAccess.Static));
            layers.Name = "tile map layers";
        }

        // Only when they are not what is up there already. An upload while the GPU is still reading the last one is
        // a whole new buffer (made, bound, named, the old one thrown away), and a map that is drawn in two goes
        // (the foreground after everybody else) came through here twice a frame with the very same layers. A
        // camera that stands still, a menu, a paused fight, all of it for nothing
        ReadOnlySpan<Layer> wanted = CollectionsMarshal.AsSpan(layerList);
        if (!MemoryMarshal.AsBytes(wanted).SequenceEqual(MemoryMarshal.AsBytes(uploadedLayers.AsSpan(0, uploadedLayerCount))))
        {
            // Over what is there when there are as many layers as there were, which is every time but the first.
            // A layer that drifts or a camera that moves changes them every frame, and uploading them was a new
            // buffer every frame for a few hundred bytes, made, given memory, bound and named, with the old one
            // thrown away after it. Written over in place they go up in order with the draws, and the culling
            // that comes next ends the pass anyway, so it breaks nothing that wasn't about to be broken
            if (wanted.Length == uploadedLayerCount)
                layers.Update(wanted);
            else
                layers.Upload(wanted);

            if (uploadedLayers.Length < wanted.Length)
                uploadedLayers = new Layer[wanted.Length];
            wanted.CopyTo(uploadedLayers);
            uploadedLayerCount = wanted.Length;
        }

        // Whether the chunks still line up with what was packed last time
        bool repack = !packedOnce;
        for (int i = 0; i < shown.Count && !repack; i++)
        {
            foreach (TileMapBatch batch in shown[i].Layer.Batches)
            {
                if (batch.First < 0 || batch.Packed != batch.Instances.Count) repack = true;
            }
        }

        if (repack) Pack(shown, textureOf);
        else Patch(shown, textureOf);

        return chunkList.Count > 0;
    }

    /// <summary>Helper method to lay every tile of every layer out in one buffer, chunk after chunk, and the chunks with them.</summary>
    private void Pack(List<(TileMapLayer Layer, Layer Settings, bool Foreground)> shown, Func<string, TileMapTexture> textureOf)
    {
        packed.Clear();
        chunkList.Clear();

        for (int layerIndex = 0; layerIndex < shown.Count; layerIndex++)
        {
            var (layer, _, foreground) = shown[layerIndex];
            foreach (TileMapBatch batch in layer.Batches)
            {
                batch.First = packed.Count;
                batch.Packed = batch.Instances.Count;
                batch.NeedsUpload = false;

                if (batch.Instances.Count == 0) continue;

                TileMapTexture texture = textureOf(batch.ImagePath);
                foreach (ref readonly TileInstance instance in CollectionsMarshal.AsSpan(batch.Instances))
                    packed.Add(Convert(instance, (uint)layerIndex, texture));

                chunkList.Add(new Chunk
                {
                    Min = batch.Min,
                    Max = batch.Max,
                    First = (uint)batch.First,
                    Count = (uint)batch.Instances.Count,
                    LayerIndex = (uint)layerIndex,
                    Foreground = foreground ? 1u : 0u
                });
            }
        }

        packedOnce = true;
        if (packed.Count == 0) return;

        // The tiles live on the card and are patched in place from here on, so they get room to grow into
        if (tiles is null || tileCapacity < packed.Count)
        {
            tiles?.Dispose();
            tileCapacity = Math.Max(packed.Count, tileCapacity * 2);
            tiles = GpuBuffer.Create(new BufferDescription(BufferUsage.Storage, BufferAccess.Dynamic, (nuint)(tileCapacity * Marshal.SizeOf<Tile>())));
            tiles.Name = "tile map tiles";
        }

        tiles.Update<Tile>(CollectionsMarshal.AsSpan(packed));

        chunks ??= GpuBuffer.Create(new BufferDescription(BufferUsage.Storage, BufferAccess.Static));
        chunks.Name = "tile map chunks";
        chunks.Upload<Chunk>(CollectionsMarshal.AsSpan(chunkList));

        nuint commandBytes = (nuint)(Math.Max(1, chunkList.Count) * 16);
        if (commands is null || commands.Size < commandBytes)
        {
            commands?.Dispose();
            commands = GpuBuffer.Create(new BufferDescription(BufferUsage.Storage | BufferUsage.Indirect, BufferAccess.Dynamic, commandBytes));
            commands.Name = "tile map draw commands";
        }

        count ??= GpuBuffer.Create(new BufferDescription(BufferUsage.Storage | BufferUsage.Indirect, BufferAccess.Dynamic, 16));
        count.Name = "tile map draw count";
    }

    /// <summary>Helper method to send up only the batches that changed, in place.</summary>
    private void Patch(List<(TileMapLayer Layer, Layer Settings, bool Foreground)> shown, Func<string, TileMapTexture> textureOf)
    {
        if (tiles is null) return;

        for (int layerIndex = 0; layerIndex < shown.Count; layerIndex++)
        {
            foreach (TileMapBatch batch in shown[layerIndex].Layer.Batches)
            {
                if (!batch.NeedsUpload || batch.Instances.Count == 0) continue;
                batch.NeedsUpload = false;

                TileMapTexture texture = textureOf(batch.ImagePath);
                var span = CollectionsMarshal.AsSpan(batch.Instances);
                // Into the same array every time. A layer of water animates every frame, and a fresh array of
                // every tile of it every frame was forty kilobytes of garbage a batch for the collector to weep over
                if (patching.Length < span.Length)
                    patching = new Tile[Math.Max(span.Length, patching.Length * 2)];
                for (int i = 0; i < span.Length; i++) patching[i] = Convert(span[i], (uint)layerIndex, texture);

                tiles.Update<Tile>(patching.AsSpan(0, span.Length), (nint)(batch.First * Marshal.SizeOf<Tile>()));
            }
        }
    }

    private static Tile Convert(in TileInstance instance, uint layerIndex, TileMapTexture texture) => new()
    {
        Position = instance.Position,
        Size = instance.Size,
        Source = instance.Source,
        Rotation = instance.Rotation,
        Flip = instance.Flip,
        LayerIndex = layerIndex,
        Albedo = texture.Albedo.BindlessIndex,
        Normal = texture.Normal.IsValid ? texture.Normal.BindlessIndex : SpriteItemNoTexture,
        Specular = texture.Specular.IsValid ? texture.Specular.BindlessIndex : SpriteItemNoTexture,
        Occlusion = texture.Occlusion.IsValid ? texture.Occlusion.BindlessIndex : SpriteItemNoTexture,
        TexelSize = Vector2.One / Vector2.Max(Vector2.One, texture.Size)
    };

    private const uint SpriteItemNoTexture = 0xFFFF;

    /// <summary>
    /// Draws the layers of one kind (the foreground ones, the rest, or all) that are in view. The culling happens on the
    /// GPU and so does the deciding how many draws there are.
    /// </summary>
    /// <param name="foreground">True for the foreground layers, false for the others, null for all of them.</param>
    public void Draw(bool? foreground, Vector2 viewMin, Vector2 viewMax, Camera camera)
    {
        if (tiles is null || chunks is null || commands is null || count is null || layers is null || chunkList.Count == 0) return;
        if (drawShader is null || cullShader is null) return;

        var device = GraphicsDevice.Current;
        CameraBlock.Use(camera);

        // Which chunks are in view, worked out by the GPU into a draw for every chunk, empty for one that isn't,
        // in the order the chunks were packed, which is the order of the layers
        cullShader.Bind();
        cullShader.SetUniform("uChunkCount", (uint)chunkList.Count);
        cullShader.SetUniform("uForeground", foreground is null ? 2u : foreground.Value ? 1u : 0u);
        cullShader.SetUniform("uViewMin", viewMin);
        cullShader.SetUniform("uViewMax", viewMax);
        device.BindStorageBuffer(LAYERS_BINDING, layers);
        device.BindStorageBuffer(CHUNKS_BINDING, chunks);
        device.BindStorageBuffer(COMMANDS_BINDING, commands);
        device.BindStorageBuffer(COUNT_BINDING, count);
        device.Dispatch((uint)((chunkList.Count + 63) / 64));
        device.Barrier(BarrierTargets.ShaderStorage | BarrierTargets.VertexAttributes);

        // And the draws themselves, one a chunk
        drawShader.Bind();
        device.BindStorageBuffer(LAYERS_BINDING, layers);
        device.BindStorageBuffer(TILES_BINDING, tiles);
        device.BindStorageBuffer(COMMANDS_BINDING, null);
        device.BindStorageBuffer(COUNT_BINDING, null);
        device.BindVertexArray(null);
        device.DrawIndirectCount(Topology.Triangles, commands, 0, count, 0, (uint)chunkList.Count);
    }

    private static bool EnsureShaders()
    {
        if (drawShader is not null && cullShader is not null) return true;

        var shaders = GameEngine.Instance.ObjectManager.Shaders;

        // By name, so it is the same one for every map there ever is and nobody frees it from under the others
        if (!shaders.TryCreateOrGet("tilemap", ShaderDescription.FromPath("shaders/tilemap", "tilemap"), out var draw))
        {
            Log.Error(draw.Message);
            return false;
        }

        if (!shaders.TryCreateOrGet("tilemap_cull", ShaderDescription.FromPath("shaders/tilemap", "tilemap_cull"), out var cull))
        {
            Log.Error(cull.Message);
            return false;
        }

        drawShader = new Technique(draw.Asset);
        cullShader = new Technique(cull.Asset);
        return true;
    }

    public void Dispose()
    {
        layers?.Dispose();
        tiles?.Dispose();
        chunks?.Dispose();
        commands?.Dispose();
        count?.Dispose();
        layers = tiles = chunks = commands = count = null;
    }
}
