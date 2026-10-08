using System.Numerics;

using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

using Texture = Horizon.OpenGL.Assets.Texture;

namespace Horizon.Rendering.Lighting;

/// <summary>
/// What in the world is solid as far as light goes: a grid over the world in which every cell either lets light through
/// or blocks it. A <see cref="DeferredRenderer2D"/> that is given one has its lights cast shadows.
/// The grid is the same no matter where the camera is, so what is off screen still casts its shadow onto what isn't.
/// A tile map fits this exactly: one cell for every tile, solid where the tile is.
/// <para>
/// Cells are set from the updates. The renderer it is given to publishes the grid along with the rest of its lighting
/// at the end of every tick (a copy, only when something changed), and frames drawn alongside the simulation show that,
/// so a wall that goes up throws its shadow the same frame it is drawn rather than a tick early, or half of it.
/// </para>
/// </summary>
public sealed class OcclusionMap2D : IDisposable
{
    private readonly byte[] cells;
    private Texture? texture;

    // Goes up whenever a cell changes, which is how a capture and the texture know whether they are behind
    private int version;
    private int uploadedVersion = -1;

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

    public OcclusionMap2D(int width, int height, Vector2 origin, Vector2 cellSize)
    {
        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);
        Origin = origin;
        CellSize = cellSize;

        cells = new byte[Width * Height];
    }

    /// <summary>
    /// Whether a cell blocks light. Cells outside of the grid never do, setting one is ignored.
    /// </summary>
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

    /// <summary>
    /// Sets whether the cell a position of the world falls into blocks light.
    /// </summary>
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
    /// GL thread. The grid as a texture with a texel for every cell, as the frame that is being drawn shows it (as it
    /// is, with the simulation standing still). Null if it couldn't be made.
    /// </summary>
    internal unsafe Texture? GetTexture()
    {
        if (texture is null)
        {
            if (!GameEngine
                    .Instance
                    .ObjectManager
                    .Textures
                    .TryCreate(
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
                Horizon.Graphics.GraphicsDevice.Current.UploadTexels(texture, 0, 0, (uint)Width, (uint)Height, Horizon.Graphics.TexelFormat.R8, data);
        }

        return texture;
    }

    public void Dispose()
    {
        if (texture is null) return;

        texture.Dispose();
        texture = null;
    }
}
