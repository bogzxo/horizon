// Decides which chunks of a tile map are drawn this frame, on the GPU. Every chunk is a run of tiles of one layer
// that sit together, with the box around them. Every chunk gets a draw of the one quad over its tiles written into
// the command buffer at its own place, and a chunk that is out of view (or of the wrong layers) gets one of no
// instances, which costs the GPU nothing. The draws stay in the order the chunks were packed in, which is layer by
// layer, and that order is the whole point. Handing out slots with an atomic put the draws in whatever order the
// threads got there, which was a different order every frame, so the lantern of one layer drew over the glowing
// lantern of the one above it, or not, depending on the frame. The CPU never looks at the tiles again once they are built.

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

// One uint, how many draws there are, which is every chunk
BIND_BUFFER(5) RWStructuredBuffer<uint> drawCount;

[shader("compute")]
[numthreads(64, 1, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= params.uChunkCount) return;
    if (id.x == 0) drawCount[0] = params.uChunkCount;

    Chunk chunk = chunks[id.x];
    bool drawn = chunk.count != 0 && (params.uForeground == 2u || chunk.foreground == params.uForeground);

    // Out of sight, out of the frame
    float2 offset = layers[chunk.layer].offset;
    if (chunk.max.x + offset.x < params.uViewMin.x || chunk.min.x + offset.x > params.uViewMax.x ||
        chunk.max.y + offset.y < params.uViewMin.y || chunk.min.y + offset.y > params.uViewMax.y) drawn = false;

    DrawCommand command;
    command.vertexCount = 6;
    command.instanceCount = drawn ? chunk.count : 0u;
    command.firstVertex = 0;
    command.firstInstance = chunk.first;
    commands[id.x] = command;
}
