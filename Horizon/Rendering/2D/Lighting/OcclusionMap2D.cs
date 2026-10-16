using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Graphics;

namespace Horizon.Rendering.Lighting;

/// <summary>
/// What in the world is solid as far as light goes, a grid over the world in which every cell either lets light through
/// or blocks it. A <see cref="DeferredRenderer2D"/> that is given one has its lights cast shadows.
/// The grid is the same no matter where the camera is, so what is off screen still casts its shadow onto what isn't.
/// A tile map fits this exactly, one cell for every tile, solid where the tile is.
/// <para>
/// Cells are set from the updates. The renderer it is given to publishes the grid along with the rest of its lighting
/// at the end of every tick (a copy, only when something changed), and frames drawn alongside the simulation show that,
/// so a wall that goes up throws its shadow the same frame it is drawn rather than a tick early, or half of it.
/// </para>
/// <para>
/// What the shaders read is not the grid but a signed distance field made from it (shaders/lighting/sdf_rows.slang and
/// sdf_columns.slang), a few texels to a cell, so a ray towards a light takes a handful of steps rather than one per
/// cell. It is made anew on the GPU whenever the grid changes, which is rarely, or worked out ahead on the CPU
/// (<see cref="Prepare"/>) for a map that is loaded on a worker and has to come up without a hitch.
/// </para>
/// </summary>
public sealed class OcclusionMap2D : IDisposable
{
    /// <summary>How many texels of the distance field a cell is, each way, unless the map was made with another number.</summary>
    public const int FIELD_TEXELS_PER_CELL = 8;

    /// <summary>
    /// How many texels of the distance field a cell of this map is, each way. Eight for a grid of tiles, so the
    /// field is finer than the squares it is made of, one for a grid that is fine already (a cell a texel of the
    /// art, <c>TileMap.CreateOcclusion</c>).
    /// </summary>
    public int FieldTexelsPerCell { get; }

    private readonly byte[] cells;
    private Texture? texture, field, rows;

    // Goes up whenever a cell changes, which is how a capture and the textures know whether they are behind
    private int version;
    private int uploadedVersion = -1, fieldVersion = -1;

    private static Technique? rowsShader, columnsShader;

    // The distance field worked out on the CPU (Prepare) and which version of the grid it is of, sent up instead of
    // made on the GPU the first time it is wanted
    private Half[]? preparedField;
    private int preparedVersion = -1;

    /// <summary>What <see cref="Prepare"/> came to, until it is sent up. For the tests.</summary>
    internal Half[]? PreparedField => preparedField;

    // The cores Prepare takes, all but the ones the render thread and the simulation want while a map loads on a worker
    private static readonly ParallelOptions Spare = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 3) };

    private sealed class CapturedCells
    {
        public byte[] Cells = [];
        public int Version = -1;
    }

    private readonly SnapshotBuffer<CapturedCells> captured = new(static () => new CapturedCells());

    /// <summary>How many cells the grid is across and up.</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>Where in the world the bottom left corner of the first cell is.</summary>
    public Vector2 Origin { get; }

    /// <summary>The size of a cell, in world units.</summary>
    public Vector2 CellSize { get; }

    /// <summary>The size of a texel of the distance field, in world units.</summary>
    public Vector2 FieldTexelSize => CellSize / FieldTexelsPerCell;

    /// <summary>How many texels the distance field is across and up.</summary>
    public Vector2 FieldSize => new(Width * FieldTexelsPerCell, Height * FieldTexelsPerCell);

    public OcclusionMap2D(int width, int height, Vector2 origin, Vector2 cellSize, int fieldTexelsPerCell = FIELD_TEXELS_PER_CELL)
    {
        FieldTexelsPerCell = Math.Max(fieldTexelsPerCell, 1);
        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);
        Origin = origin;
        CellSize = cellSize;

        cells = new byte[Width * Height];
    }

    /// <summary>Whether a cell blocks light. Cells outside of the grid never do, setting one is ignored.</summary>
    public bool this[int x, int y]
    {
        get => Contains(x, y) && cells[x + y * Width] != 0;
        set
        {
            if (!Contains(x, y)) return;

            byte solid = value ? byte.MaxValue : byte.MinValue;
            if (cells[x + y * Width] == solid) return;

            cells[x + y * Width] = solid;
            version++;
        }
    }

    /// <summary>Sets whether the cell a position of the world falls into blocks light.</summary>
    public void Set(Vector2 position, bool solid)
    {
        Vector2 cell = (position - Origin) / CellSize;
        this[(int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Y)] = solid;
    }

    private bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    /// <summary>
    /// Publishes the grid as it is, for the frames that are drawn alongside the simulation. Simulation thread, at the
    /// end of every tick, by the renderer it was given to. Copied only into a slot that has an older one.
    /// </summary>
    internal void Capture()
    {
        if (captured.BeginPublish() is not { } into || into.Version == version)
            return;

        if (into.Cells.Length != cells.Length)
            into.Cells = new byte[cells.Length];

        cells.CopyTo(into.Cells, 0);
        into.Version = version;
    }

    /// <summary>
    /// Render thread. The grid as a texture with a texel for every cell, as the frame that is being drawn shows it (as it
    /// is, with the simulation standing still). Null if it couldn't be made.
    /// </summary>
    internal unsafe Texture? GetTexture()
    {
        // Gone, and not to be made again by a frame that still had it (it was the one a renderer had last tick)
        if (IsDisposed) return null;

        if (texture is null)
        {
            if (!GameEngine.Instance.ObjectManager.Textures.TryCreate(
                    new TextureDescription
                    {
                        Width = (uint)Width,
                        Height = (uint)Height,
                        Definition = TextureDefinition.RedUnsignedByteNearest
                    },
                    out var result))
            {
                return null;
            }

            texture = result.Asset;
            texture.Name = "occlusion map";
            uploadedVersion = -1;
        }

        // What the frame shows, the newest of its two ticks the way the rest of the lighting's settings are
        RenderFrame frame = RenderFrame.Active;
        (byte[] shown, int shownVersion) = frame.IsDecoupled && captured.TryGet(frame, out CapturedCells current)
            ? (current.Cells, current.Version)
            : (cells, version);

        if (shownVersion != uploadedVersion && shown.Length == cells.Length)
        {
            uploadedVersion = shownVersion;

            fixed (byte* data = shown)
                GraphicsDevice.Current.UploadTexels(texture, 0, 0, (uint)Width, (uint)Height, TexelFormat.R8, data);
        }

        return texture;
    }

    /// <summary>
    /// Render thread. The signed distance field of the grid, in world units, negative inside of a wall. Made anew on
    /// the GPU when the grid has changed since. Null if it couldn't be made.
    /// </summary>
    internal unsafe Texture? GetField()
    {
        if (GetTexture() is not { } grid) return null;
        if (field is not null && fieldVersion == uploadedVersion) return field;

        var objects = GameEngine.Instance.ObjectManager;
        uint width = (uint)FieldSize.X, height = (uint)FieldSize.Y;

        if (field is null)
        {
            if (!objects.Textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = TextureDefinition.DistanceField }, out var made))
                return null;
            field = made.Asset;
            field.Name = "occlusion distance field";
        }

        var device = GraphicsDevice.Current;

        // Worked out ahead for the grid as it is shown, only sent up then. Let go of after, a grid that changes is
        // made anew on the GPU
        if (preparedField is { } ready && preparedVersion == uploadedVersion)
        {
            fixed (Half* data = ready)
                device.UploadTexels(field, 0, 0, width, height, TexelFormat.R16F, data);

            preparedField = null;
            fieldVersion = uploadedVersion;
            return field;
        }

        if (!EnsureShaders()) return null;

        if (rows is null)
        {
            if (!objects.Textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = new TextureDefinition(PixelFormat.Rg16F, Smooth: false, Usage: TextureUsage.Storage) }, out var temp))
                return null;
            rows = temp.Asset;
            rows.Name = "occlusion distance rows";
        }

        uint groupsX = (width + 7) / 8, groupsY = (height + 7) / 8;

        foreach (Technique pass in new[] { rowsShader!, columnsShader! })
        {
            pass.Bind();
            pass.SetUniform("uSize", FieldSize);
            pass.SetUniform("uCells", new Vector2(Width, Height));
            pass.SetUniform("uTexelWorld", MathF.Min(FieldTexelSize.X, FieldTexelSize.Y));
            pass.SetUniform("uTexelsPerCell", FieldTexelsPerCell);
            grid.Bind(0);
            device.BindStorageImage(0, rows);
            device.BindStorageImage(1, field);
            device.Dispatch(groupsX, groupsY);
            device.Barrier(BarrierTargets.ShaderImages);
            pass.Unbind();
        }

        device.BindStorageImage(0, null);
        device.BindStorageImage(1, null);
        fieldVersion = uploadedVersion;
        return field;
    }

    /// <summary>
    /// Works the distance field out on the CPU, what the shaders would make of the grid as it is now, so the first
    /// frame that wants it only sends it up. Any thread, before the map is given to a renderer (a stage loaded on a
    /// worker, say). The shaders go by brute force, every texel of a column for every texel of it, which is fine for
    /// a grid that changes in a game and on a 2304 square map was two tenths of a second of the GPU doing nothing
    /// else, the frame a map came up. Here it is the distance transform done properly, the lower envelope of
    /// parabolas (Felzenszwalb and Huttenlocher), linear in the texels and spread over the cores. A grid that is the
    /// same as one prepared a moment ago (the fight on the map its preview showed) gets the same field straight away.
    /// </summary>
    public void Prepare()
    {
        if (preparedField is not null && preparedVersion == version) return;

        int width = (int)FieldSize.X, height = (int)FieldSize.Y;
        int stamp = version;

        var key = Key();
        if (shared.TryGetValue(key, out Half[]? known))
        {
            preparedField = known;
            preparedVersion = stamp;
            return;
        }

        // Tens of megabytes on a big map, out of the pool and back, garbage that size stops every thread of the game
        // for a big collection
        float[] toSolid = SquaredDistances(width, height, solid: true);
        float[] toOpen = SquaredDistances(width, height, solid: false);

        // The same as the end of sdf_columns.slang. The edge of a wall is half a texel past the last solid texel
        // either way, negative inside, in world units
        float texelWorld = MathF.Min(FieldTexelSize.X, FieldTexelSize.Y);
        var made = new Half[width * height];
        Parallel.For(0, height, Spare, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = x + y * width;
                float distance = SolidAt(x, y) ? -(MathF.Sqrt(toOpen[i]) - 0.5f) : MathF.Sqrt(toSolid[i]) - 0.5f;
                made[i] = (Half)(distance * texelWorld);
            }
        });

        System.Buffers.ArrayPool<float>.Shared.Return(toSolid);
        System.Buffers.ArrayPool<float>.Shared.Return(toOpen);

        if (shared.Count >= MAX_SHARED) shared.Clear();
        shared[key] = made;

        preparedField = made;
        preparedVersion = stamp;
    }

    // Fields prepared before by what they were made of, a few of them, the biggest map's is ten megabytes. Only ever
    // read once they are in, so the same one going up for two maps is fine
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int Width, int Height, int PerCell, Vector2 Texel, int Hash, long Solid), Half[]> shared = new();
    private const int MAX_SHARED = 4;

    // What the field is made of, the size and the cells, as a hash and the sum of where the solid cells are as well,
    // two maps that come to the same key by chance would have to agree on both
    private (int, int, int, Vector2, int, long) Key()
    {
        var hash = new HashCode();
        hash.AddBytes(cells);

        long solid = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] != 0) solid += i + 1;
        }

        return (Width, Height, FieldTexelsPerCell, FieldTexelSize, hash.ToHashCode(), solid);
    }

    private bool SolidAt(int x, int y)
    {
        int cellX = Math.Min(x / FieldTexelsPerCell, Width - 1), cellY = Math.Min(y / FieldTexelsPerCell, Height - 1);
        return cells[cellX + cellY * Width] != 0;
    }

    // What sdf_rows.slang leaves for the columns, with nothing in the row being as good as a million texels away
    private const float NOWHERE = 1e6f;

    // The squared distance (in texels) from every texel of the field to the nearest one that is solid, or that
    // isn't. Rows first, how far along its row the nearest one is, then down every column the smallest of that
    // squared plus how far up or down, which is the exact euclidean distance
    private float[] SquaredDistances(int width, int height, bool solid)
    {
        float[] along = System.Buffers.ArrayPool<float>.Shared.Rent(width * height);

        Parallel.For(0, height, Spare, y =>
        {
            int row = y * width;
            float last = -NOWHERE;
            for (int x = 0; x < width; x++)
            {
                if (SolidAt(x, y) == solid) last = x;
                along[row + x] = x - last;
            }

            last = width + NOWHERE;
            for (int x = width - 1; x >= 0; x--)
            {
                if (SolidAt(x, y) == solid) last = x;
                along[row + x] = MathF.Min(MathF.Min(along[row + x], last - x), NOWHERE);
            }
        });

        Parallel.For(0, width, Spare, () => new Envelope(height), (x, _, envelope) =>
        {
            for (int y = 0; y < height; y++)
            {
                float d = along[x + y * width];
                envelope.Heights[y] = (double)d * d;
            }

            envelope.Lower(height);

            for (int y = 0; y < height; y++)
                along[x + y * width] = (float)envelope.Distances[y];

            return envelope;
        }, static _ => { });

        return along;
    }

    // The scratch of one column of the lower envelope, one a core
    private sealed class Envelope(int size)
    {
        public readonly double[] Heights = new double[size];
        public readonly double[] Distances = new double[size];
        private readonly int[] apexes = new int[size];
        private readonly double[] bounds = new double[size + 1];

        // For every y the smallest (y - q)^2 + Heights[q] over every q, the parabolas the heights stand for laid one
        // over the other and only the lowest kept, then read off left to right
        public void Lower(int n)
        {
            int k = 0;
            apexes[0] = 0;
            bounds[0] = double.NegativeInfinity;
            bounds[1] = double.PositiveInfinity;

            for (int q = 1; q < n; q++)
            {
                double s;
                while (true)
                {
                    int p = apexes[k];
                    s = (Heights[q] + (double)q * q - (Heights[p] + (double)p * p)) / (2.0 * (q - p));
                    if (s > bounds[k]) break;
                    k--;
                }

                k++;
                apexes[k] = q;
                bounds[k] = s;
                bounds[k + 1] = double.PositiveInfinity;
            }

            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (bounds[k + 1] < q) k++;
                int p = apexes[k];
                Distances[q] = (double)(q - p) * (q - p) + Heights[p];
            }
        }
    }

    private static bool EnsureShaders()
    {
        if (rowsShader is not null && columnsShader is not null) return true;

        var shaders = GameEngine.Instance.ObjectManager.Shaders;
        if (!shaders.TryCreateOrGet("sdf_rows", ShaderDescription.FromPath("shaders/lighting", "sdf_rows"), out var rows)) return false;
        if (!shaders.TryCreateOrGet("sdf_columns", ShaderDescription.FromPath("shaders/lighting", "sdf_columns"), out var columns)) return false;

        rowsShader = new Technique(rows.Asset);
        columnsShader = new Technique(columns.Asset);
        return true;
    }

    /// <summary>Whether it has been disposed, after which it has no textures and makes none.</summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        texture?.Dispose();
        field?.Dispose();
        rows?.Dispose();
        texture = field = rows = null;

        preparedField = null;
    }
}
