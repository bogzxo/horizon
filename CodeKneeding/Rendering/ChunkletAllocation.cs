namespace CodeKneading.Rendering;

internal readonly struct ChunkletAllocation
{
    public readonly int DrawCommandIndex { get; init; }
    
    public readonly BufferRegion VertexRegion { get; init; }
    public readonly BufferRegion ElementRegion { get; init; }

    public readonly BufferRegion DrawIndirectRegion { get; init; }
    public readonly BufferRegion CullIndirectRegion { get; init; }
    public readonly BufferRegion ChunkDataRegion { get; init; }
}
