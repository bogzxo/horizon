using Bogz.Logging.Loggers;

using CodeKneading.Voxel;

using Horizon.Engine;
using Horizon.OpenGL.Assets;

using Silk.NET.OpenGL;

namespace CodeKneading.Rendering;

internal class ChunkletManager
{
    // ### Heaps ###
    public BufferDataHeap VertexHeap { get; init; }
    public BufferDataHeap ElementHeap { get; init; }
    public BufferDataHeap DrawIndirectHeap { get; init; }
    public BufferDataHeap CullIndirectHeap { get; init; }
    public BufferDataHeap DrawDataHeap { get; init; }

    public int Count { get => VertexHeap.ActiveAllocations * 6; }

    public ChunkletManager(
        in BufferObject vertexBuffer,
        in BufferObject elementBuffer,
        in BufferObject indirectBuffer,
        in BufferObject cullIndirectBuffer,
        in BufferObject chunkDataBuffer)
    {
        // generate heap managers
        VertexHeap = new BufferDataHeap(vertexBuffer);
        ElementHeap = new BufferDataHeap(elementBuffer);

        DrawIndirectHeap = new BufferDataHeap(indirectBuffer);
        CullIndirectHeap = new BufferDataHeap(cullIndirectBuffer);
        DrawDataHeap = new BufferDataHeap(chunkDataBuffer);
    }

    public void Free(in ChunkletAllocation alloc)
    {
        VertexHeap.Return(alloc.VertexRegion);
        ElementHeap.Return(alloc.ElementRegion);
        DrawIndirectHeap.Return(alloc.DrawIndirectRegion);
        CullIndirectHeap.Return(alloc.CullIndirectRegion);
        DrawDataHeap.Return(alloc.ChunkDataRegion);
    }

    public void FreeAll()
    {
        VertexHeap.Clear();
        ElementHeap.Clear();
        DrawIndirectHeap.Clear();
        CullIndirectHeap.Clear();
        DrawDataHeap.Clear();
    }


    public bool TryGetChunkletAlloc(in int vertexSize, in int elementSize, out ChunkletAllocation alloc)
    {
        if (!TryGetRegions(
            vertexSize,
            elementSize,
            
            out var vReg,
            out var eReg,
            out var drawReg,
            out var cullReg,
            out var dataReg
            ))
        {
            alloc = default;
            return false;
        }

        alloc = new ChunkletAllocation() {
            ElementRegion = eReg,
            VertexRegion = vReg,

            ChunkDataRegion = dataReg,
            DrawIndirectRegion = drawReg,
            CullIndirectRegion = cullReg,
            
            DrawCommandIndex = VertexHeap.ActiveAllocations,
        };

        return true;
    }

    public unsafe bool TryGetRegions(
        in int vertexSize,
        in int elementSize,
        
        out BufferRegion vertexRegion,
        out BufferRegion elementRegion,
        
        out BufferRegion drawIndirectRegion,
        out BufferRegion cullIndirectRegion,
        out BufferRegion chunkDataRegion
        )
    {
        vertexRegion = elementRegion = drawIndirectRegion = cullIndirectRegion = chunkDataRegion = null!;

        if (!VertexHeap.TryGetRegion(vertexSize, out vertexRegion))
        {
            ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Buffer vertex heap full!");
            return false;
        }

        if (!ElementHeap.TryGetRegion(elementSize, out elementRegion))
        {
            ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Buffer element heap full!");
            return false;
        }

        if (!DrawIndirectHeap.TryGetRegion(sizeof(TileChunkDrawCommand) * 6, out drawIndirectRegion))
        {
            ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Buffer draw heap full!");
            return false;
        }

        if (!CullIndirectHeap.TryGetRegion(sizeof(TileChunkDrawCommand) * 6, out cullIndirectRegion))
        {
            ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Buffer cull heap full!");
            return false;
        }

        if (!DrawDataHeap.TryGetRegion(sizeof(TileChunkData) * 6, out chunkDataRegion))
        {
            ConcurrentLogger.Instance.Log(Bogz.Logging.LogLevel.Error, "Buffer data heap full!");
            return false;
        }

        return true;
    }

    public unsafe bool TryUploadMeshData(ref readonly ChunkMeshData result)
    {
        int vSize = result.VerticesMemory.Span.Length * sizeof(VoxelVertex);
        int eSize = result.ElementsMemory.Span.Length * sizeof(ushort);

        if (!TryGetChunkletAlloc(vSize, eSize, out var alloc))
            return false;

        // pass buffer region refs into chunk
        result.Chunk.SetMeshlet(ref alloc);

        // upload vertices
        alloc.VertexRegion.BufferData(result.VerticesMemory.Span);
        // upload elements
        alloc.ElementRegion.BufferData(result.ElementsMemory.Span);

        // temp variables to store offets for draw commands
        int vertsOffset = alloc.VertexRegion.OffsetInBytes / sizeof(VoxelVertex);
        uint indsOffset = (uint)(alloc.ElementRegion.OffsetInBytes / sizeof(ushort));

        TileChunkDrawCommand[] drawCommands = new TileChunkDrawCommand[6];
        TileChunkData[] chunkData = new TileChunkData[6];
        
        // generate draw commands for each cube face
        for (int i = 0; i < 6; i++)
        {
            drawCommands[i] = new TileChunkDrawCommand
            {
                firstIndex = indsOffset,
                count = result.ElementCounts[i],
                firstVertex = vertsOffset,
                instanceCount = 1,
                baseInstance = 0,

                lodLevel = result.Chunk.LOD,
                chunkCenterX = (int)(result.ChunkData.xPos + (TileChunk.SIZE / 2)),
                chunkCenterY = (int)(result.ChunkData.yPos + (TileChunk.SIZE / 2)),
                chunkCenterZ = (int)(result.ChunkData.zPos + (TileChunk.SIZE / 2)),
            };

            chunkData[i] = result.ChunkData with
            {
                face = i
            };

            indsOffset += result.ElementCounts[i];
            vertsOffset += result.VertexCounts[i];
        }

        alloc.DrawIndirectRegion.BufferData(new ReadOnlySpan<TileChunkDrawCommand>(drawCommands));
        alloc.CullIndirectRegion.BufferData(new ReadOnlySpan<TileChunkDrawCommand>(drawCommands));
        alloc.ChunkDataRegion.BufferData(new ReadOnlySpan<TileChunkData>(chunkData));

        return true;
    }
}
