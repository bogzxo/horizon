// Decides which chunks of a tile map are drawn this frame, on the GPU. Every chunk is a run of tiles of one layer
// that sit together, with the box around them. A chunk that reaches into the view gets a draw of the one quad over
// its tiles written into the command buffer, and the count of those is what the map draws with (DrawIndirectCount).
// The CPU never looks at the tiles again once they are built.

#include <common/camera.slang>

// Must match TileMapGpu.Chunk in TileMapBatch.cs.
struct Chunk
{
    float2 min;
    float2 max;
    uint first;
    uint count;
    uint layer;
    uint foreground; // 1 for a layer that is drawn over the sprites, 0 for one behind them
};

// Must match TileMapGpu.Layer in tilemap.slang, only the offset is wanted here.
struct Layer
{
    float2 offset;
    float2 padding0;
    float padding1;
    float emissive;
    float2 padding;
    float4 tint;
};

// VkDrawIndirectCommand.
struct DrawCommand
{
    uint vertexCount;
    uint instanceCount;
    uint firstVertex;
    uint firstInstance;
};

struct Params
{
    uint uChunkCount;
    uint uForeground; // which layers to draw, 1 for the foreground ones, 0 for the rest, 2 for all of them
    float2 uViewMin;
    float2 uViewMax;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_BUFFER(2) StructuredBuffer<Layer> layers;
BIND_BUFFER(3) StructuredBuffer<Chunk> chunks;
BIND_BUFFER(4) RWStructuredBuffer<DrawCommand> commands;

// One uint, how many commands were written. Must be cleared to 0 before the dispatch.
BIND_BUFFER(5) RWStructuredBuffer<uint> drawCount;

[shader("compute")]
[numthreads(64, 1, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= params.uChunkCount) return;

    Chunk chunk = chunks[id.x];
    if (chunk.count == 0) return;
    if (params.uForeground != 2u && chunk.foreground != params.uForeground) return;

    // Out of sight, out of the frame
    float2 offset = layers[chunk.layer].offset;
    if (chunk.max.x + offset.x < params.uViewMin.x || chunk.min.x + offset.x > params.uViewMax.x ||
        chunk.max.y + offset.y < params.uViewMin.y || chunk.min.y + offset.y > params.uViewMax.y) return;

    uint slot;
    InterlockedAdd(drawCount[0], 1u, slot);

    DrawCommand command;
    command.vertexCount = 6;
    command.instanceCount = chunk.count;
    command.firstVertex = 0;
    command.firstInstance = chunk.first;
    commands[slot] = command;
}
