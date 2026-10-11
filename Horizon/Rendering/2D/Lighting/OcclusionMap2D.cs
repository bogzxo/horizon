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
/// cell. It is made anew on the GPU whenever the grid changes, which is rarely.
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
    internal Texture? GetField()
    {
        if (GetTexture() is not { } grid) return null;
        if (field is not null && fieldVersion == uploadedVersion) return field;
        if (!EnsureShaders()) return null;

        var objects = GameEngine.Instance.ObjectManager;
        uint width = (uint)FieldSize.X, height = (uint)FieldSize.Y;

        if (field is null)
        {
            if (!objects.Textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = TextureDefinition.DistanceField }, out var made))
                return null;
            field = made.Asset;
            field.Name = "occlusion distance field";

            if (!objects.Textures.TryCreate(new TextureDescription { Width = width, Height = height, Definition = new TextureDefinition(PixelFormat.Rg16F, Smooth: false, Usage: TextureUsage.Storage) }, out var temp))
                return null;
            rows = temp.Asset;
            rows.Name = "occlusion distance rows";
        }

        var device = GraphicsDevice.Current;
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

    public void Dispose()
    {
        texture?.Dispose();
        field?.Dispose();
        rows?.Dispose();
        texture = field = rows = null;
    }
}
