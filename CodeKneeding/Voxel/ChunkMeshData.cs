using Silk.NET.Maths;

namespace CodeKneading.Voxel;

internal readonly struct ChunkMeshData
{
    public readonly ReadOnlyMemory<VoxelInstance> Data { get; init; }
    public readonly Vector2D<int> ChunkPos { get; init; }
}
