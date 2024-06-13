using AutoVoxel.Data;

namespace AutoVoxel.Rendering;

public readonly struct MeshUploadInfo
{
    public readonly ReadOnlyMemory<ChunkVertex> Data { get; init; }
    public readonly uint[] SliceOffsets { get; init; }
    public readonly int Index { get; init; }
    public readonly int OffsetX { get; init; }
    public readonly int OffsetZ { get; init; }
}
