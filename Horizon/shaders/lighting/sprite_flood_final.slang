// Fused final three jump-flood passes (steps 4, 2, 1) of the sprite shadows in shared memory.
// Bit-identical to running sprite_flood.slang with step=4, step=2, step=1 back to back, with one
// dispatch and one barrier instead of three.

struct Params
{
    float2 uCells;   // the grid, in cells
    int uStep;       // unused (always 4, 2, 1); kept to match sprite_flood Params layout
    int uScale;      // picture texels to a cell, for measuring
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_IMAGE(0) [[vk::image_format("rg32ui")]] RWTexture2D<uint2> uIn;
BIND_IMAGE(1) [[vk::image_format("rg32ui")]] RWTexture2D<uint2> uOut;

static const uint NONE = 0xFFFFFFFFu;

// 16x16 output tile + 7-cell apron on each side (4 + 2 + 1) = 30x30.
// Double-buffered in LDS: 2 * 30 * 30 * 8 = 14,400 bytes (< 16 KiB minimum Vulkan shared memory).
groupshared uint2 tileA[30][30];
groupshared uint2 tileB[30][30];

float2 unpack(uint seed)
{
    return float2(float(seed & 0xFFFFu), float(seed >> 16));
}

float squared(float2 from, uint seed)
{
    float2 d = unpack(seed) - from;
    return dot(d, d);
}

uint2 floodFromTileA(int lx, int ly, int2 cell, int step)
{
    float2 here = (float2(cell) + 0.5) * float(params.uScale) - 0.5;
    uint2 best = tileA[ly][lx];
    float2 bestSquared = float2(
        best.x == NONE ? 1e30 : squared(here, best.x),
        best.y == NONE ? 1e30 : squared(here, best.y));

    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0) continue;
            uint2 theirs = tileA[ly + y * step][lx + x * step];
            if (theirs.x != NONE)
            {
                float d = squared(here, theirs.x);
                if (d < bestSquared.x) { bestSquared.x = d; best.x = theirs.x; }
            }
            if (theirs.y != NONE)
            {
                float d = squared(here, theirs.y);
                if (d < bestSquared.y) { bestSquared.y = d; best.y = theirs.y; }
            }
        }
    }

    return best;
}

uint2 floodFromTileB(int lx, int ly, int2 cell, int step)
{
    float2 here = (float2(cell) + 0.5) * float(params.uScale) - 0.5;
    uint2 best = tileB[ly][lx];
    float2 bestSquared = float2(
        best.x == NONE ? 1e30 : squared(here, best.x),
        best.y == NONE ? 1e30 : squared(here, best.y));

    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0) continue;
            uint2 theirs = tileB[ly + y * step][lx + x * step];
            if (theirs.x != NONE)
            {
                float d = squared(here, theirs.x);
                if (d < bestSquared.x) { bestSquared.x = d; best.x = theirs.x; }
            }
            if (theirs.y != NONE)
            {
                float d = squared(here, theirs.y);
                if (d < bestSquared.y) { bestSquared.y = d; best.y = theirs.y; }
            }
        }
    }

    return best;
}

[shader("compute")]
[numthreads(16, 16, 1)]
void csMain(uint3 groupId : SV_GroupID, uint3 localId : SV_GroupThreadID)
{
    int2 cells = int2(params.uCells);
    int2 origin = int2(groupId.xy) * 16 - 7;
    uint flat = localId.y * 16u + localId.x;

    // Load 30x30 tile with OOB clamped to NONE
    for (uint idx = flat; idx < 900u; idx += 256u)
    {
        int ly = int(idx / 30u);
        int lx = int(idx - uint(ly) * 30u);
        int2 c = origin + int2(lx, ly);
        tileA[ly][lx] = (all(c >= 0) && all(c < cells)) ? uIn[c] : uint2(NONE, NONE);
    }
    GroupMemoryBarrierWithGroupSync();

    // Step 4 over [4..25] x [4..25] (22x22 = 484 cells)
    for (uint idx = flat; idx < 484u; idx += 256u)
    {
        int ly = 4 + int(idx / 22u);
        int lx = 4 + int(idx - uint(ly - 4) * 22u);
        int2 c = origin + int2(lx, ly);
        tileB[ly][lx] = (all(c >= 0) && all(c < cells)) ? floodFromTileA(lx, ly, c, 4) : uint2(NONE, NONE);
    }
    GroupMemoryBarrierWithGroupSync();

    // Step 2 over [6..23] x [6..23] (18x18 = 324 cells)
    for (uint idx = flat; idx < 324u; idx += 256u)
    {
        int ly = 6 + int(idx / 18u);
        int lx = 6 + int(idx - uint(ly - 6) * 18u);
        int2 c = origin + int2(lx, ly);
        tileA[ly][lx] = (all(c >= 0) && all(c < cells)) ? floodFromTileB(lx, ly, c, 2) : uint2(NONE, NONE);
    }
    GroupMemoryBarrierWithGroupSync();

    // Step 1 over the 16x16 output
    int2 cell = int2(groupId.xy) * 16 + int2(localId.xy);
    if (any(cell >= cells)) return;

    uOut[cell] = floodFromTileA(int(localId.x) + 7, int(localId.y) + 7, cell, 1);
}
