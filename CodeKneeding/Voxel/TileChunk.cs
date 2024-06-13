
using CodeKneading.Rendering;

using Silk.NET.Maths;

namespace CodeKneading.Voxel;

internal class TileChunk(in Vector3D<int> pos)
{
    public const int SIZE = 32;
    private const int SIZE_SQ = SIZE * SIZE;
    public Tile[] GroundTiles { get; init; } = new Tile[SIZE * SIZE * SIZE];
    public Vector3D<int> ChunkPosition { get; init; } = pos;
    public bool IsEmpty { get; private set; } = true;
    public int LOD { get; set; }

    /// <summary>
    /// The buffer region associated with this chunk.
    /// </summary>
    public ChunkletAllocation MeshletAllocation { get; internal set; }

    public Tile GetFloor(in int x, in int y, in int z)
    {
        if (GroundTiles == null) return Tile.OOB;
        if (x < 0 || y < 0 || z < 0) return Tile.OOB;
        if (x >= SIZE || y >= SIZE || z >= SIZE) return Tile.OOB;

        return GroundTiles[(z * SIZE * SIZE) + (y * SIZE) + x];
    }

    internal void SetMeshlet(ref readonly ChunkletAllocation alloc)
    {
        this.MeshletAllocation = alloc;
    }

    public Tile this[int x, int y, int z]
    {
        get
        {
            if (x < 0 || y < 0 || z < 0) return Tile.OOB;
            if (x >= SIZE || y >= SIZE || z >= SIZE) return Tile.OOB;

            return GroundTiles[(z * SIZE_SQ) + (y * SIZE) + x];
        }
        set
        {
            if (x < 0 || y < 0 || z < 0) return;
            if (x >= SIZE || y >= SIZE || z >= SIZE) return;

            if (IsEmpty && (value.Type != TileType.None && value.Type != TileType.OOB)) IsEmpty = false;

            GroundTiles[(z * SIZE_SQ) + (y * SIZE) + x] = value;
        }
    }
}
