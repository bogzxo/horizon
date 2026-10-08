// The first pass of the sprite shadows, see SpriteShadows.cs. Everything that was drawn as blocking light (see
// Sprite.CastsShadows) left its coverage in the green of the material attachment, and everything that lights what is
// round it says so in the blue of the material attachment. The field of distances to those is built by a jump flood over a
// grid a few picture texels coarse (uScale of them to a cell), and this pass plants the seeds of it, for every cell
// the picture texel in it that is on an edge (one that blocks with a neighbour that doesn't), of what blocks (x) and
// of what blocks or glows (y), each as its picture coordinates packed into one word, NONE for a cell without one.

struct Params
{
    float2 uSize;      // the picture, in texels
    float2 uCells;     // the seed grid, in cells
    int uScale;        // picture texels to a cell, each way
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uMaterial;
BIND_TEXTURE(1) Sampler2D uSurface;

BIND_IMAGE(0) [[vk::image_format("rg32ui")]] RWTexture2D<uint2> uSeeds;

static const uint NONE = 0xFFFFFFFFu;
static const float GLOWS_FROM = 0.2;

// Whether a texel blocks, and whether it blocks or glows
bool2 at(int2 texel, int2 size)
{
    texel = clamp(texel, int2(0, 0), size - 1);
    bool blocks = uMaterial.Load(int3(texel, 0)).g > 0.5;
    bool glows = blocks || uMaterial.Load(int3(texel, 0)).b > GLOWS_FROM;
    return bool2(blocks, glows);
}

uint pack(int2 texel)
{
    return uint(texel.x) | (uint(texel.y) << 16);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 cell = int2(id.xy);
    if (any(cell >= int2(params.uCells))) return;

    int2 size = int2(params.uSize);
    int2 first = cell * params.uScale;
    uint2 seeds = uint2(NONE, NONE);

    for (int y = 0; y < params.uScale; y++)
    {
        for (int x = 0; x < params.uScale; x++)
        {
            int2 texel = first + int2(x, y);
            if (any(texel >= size)) continue;

            bool2 here = at(texel, size);
            if (!here.x && !here.y) continue;

            // On an edge if this one does and any of the four around it doesn't
            bool2 around = at(texel + int2(1, 0), size) & at(texel - int2(1, 0), size) & at(texel + int2(0, 1), size) & at(texel - int2(0, 1), size);
            if (here.x && !around.x && seeds.x == NONE) seeds.x = pack(texel);
            if (here.y && !around.y && seeds.y == NONE) seeds.y = pack(texel);
        }
    }

    uSeeds[cell] = seeds;
}
