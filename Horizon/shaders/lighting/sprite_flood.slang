// The middle passes of the sprite shadows, a jump flood (Rong and Tan) over the seed grid of sprite_seed.slang.
// Every cell looks at the eight cells a step away and keeps the nearest seed any of them has heard of, for what
// blocks and for what blocks or glows alike. Run with the step halving from a hundred and something cells down to
// one, every cell ends up knowing the nearest seed within that reach, near enough for shadows (the march only wants
// a distance it is safe to step by). It is a dozen cheap passes where it used to be a walk down every column and
// along every row by one thread, which no GPU was built for.

struct Params
{
    float2 uCells;   // the grid, in cells
    int uStep;       // how far the cells look this pass
    int uScale;      // picture texels to a cell, for measuring
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_IMAGE(0) [[vk::image_format("rg32ui")]] RWTexture2D<uint2> uIn;
BIND_IMAGE(1) [[vk::image_format("rg32ui")]] RWTexture2D<uint2> uOut;

static const uint NONE = 0xFFFFFFFFu;

float2 unpack(uint seed)
{
    return float2(float(seed & 0xFFFFu), float(seed >> 16));
}

float squared(float2 from, uint seed)
{
    float2 d = unpack(seed) - from;
    return dot(d, d);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 cell = int2(id.xy);
    int2 cells = int2(params.uCells);
    if (any(cell >= cells)) return;

    // Measured from the middle of this cell, in picture texels
    float2 here = (float2(cell) + 0.5) * float(params.uScale) - 0.5;

    uint2 best = uIn[cell];
    float2 bestSquared = float2(
        best.x == NONE ? 1e30 : squared(here, best.x),
        best.y == NONE ? 1e30 : squared(here, best.y));

    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            if (x == 0 && y == 0) continue;
            int2 other = cell + int2(x, y) * params.uStep;
            if (any(other < 0) || any(other >= cells)) continue;

            uint2 theirs = uIn[other];
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

    uOut[cell] = best;
}
