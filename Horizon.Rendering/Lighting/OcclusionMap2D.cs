using System.Numerics;

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
/// </summary>
public sealed class OcclusionMap2D : IDisposable
{
    private readonly byte[] cells;
    private Texture? texture;
    private bool dirty = true;

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
            dirty = true;
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
    /// GL thread. The grid as a texture with a texel for every cell, brought up to date with what was set since the last time.
    /// Null if it couldn't be made.
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
            dirty = true;
        }

        if (dirty)
        {
            dirty = false;

            var gl = GameEngine.Instance.GL;

            // The rows are a byte per cell with nothing in between, the default is to expect them padded to four
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            fixed (byte* data = cells)
            {
                gl.TextureSubImage2D(
                    texture.Handle, 0, 0, 0, (uint)Width, (uint)Height, PixelFormat.Red, PixelType.UnsignedByte, data);
            }
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        }

        return texture;
    }

    public void Dispose()
    {
        if (texture is null) return;

        GameEngine.Instance.ObjectManager.Textures.Remove(texture);
        texture = null;
    }
}
