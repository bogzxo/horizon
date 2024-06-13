using CodeKneading.Rendering;

using Silk.NET.Maths;

namespace CodeKneading.Voxel;

internal readonly struct ChunkMeshData
{
    public readonly ReadOnlyMemory<VoxelVertex> VerticesMemory { get; init; }
    public readonly TileChunkData ChunkData { get; init; }
    public readonly ReadOnlyMemory<ushort> ElementsMemory { get; init; }
    public readonly int[] VertexCounts { get; init; }
    public readonly uint[] ElementCounts { get; init; }
    public readonly TileChunk Chunk { get; init; }
}
