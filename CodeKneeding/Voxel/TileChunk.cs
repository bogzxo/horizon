using CodeKneading.Rendering;

using Silk.NET.Maths;

namespace CodeKneading.Voxel;

internal readonly struct TileChunk(in Vector2D<int> pos)
{
    public const int SIZE = 32;
    public Tile[,,] GroundTiles { get; init; } = new Tile[SIZE,SIZE,SIZE];
    public readonly Vector2D<int> ChunkPosition { get; init; } = pos;

    public readonly Tile GetFloor(in int x, in int y, in int z)
    {
        if (GroundTiles == null) return Tile.Empty;
        if (x < 0 || y < 0 || z < 0) return Tile.Empty;
        if (x >= SIZE || y >= SIZE || z >= SIZE) return Tile.Empty;

        return GroundTiles[x, y, z];
    }
}
