// The last pass of the sprite shadows. With the nearest seed known for every cell of the grid (see
// sprite_flood.slang), every texel of the picture asks the four cells around it and takes the nearest seed of
// those, exactly, so the field is as sharp as the picture even though the flood ran on a coarser grid. What comes
// out is the signed distance in world units, negative inside of whatever blocks (r) and inside of whatever blocks or
// glows (g). The lights march the first the way they march the distance field of the occlusion map, the path traced
// lighting marches the second, see direct.slang, and a sprite throws a shadow as sharp as its pixels. A texel with no
// seed in reach gets uFar, which is as far as the flood looked and so a distance it is safe to step by.

struct Params
{
    float2 uSize;        // the picture, in texels
    float2 uCells;       // the seed grid, in cells
    int uScale;          // picture texels to a cell
    float uFar;          // the distance (in texels) of a texel with nothing in reach
    float uPixelWorld;   // how big a texel of the picture is in world units
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uMaterial;
BIND_TEXTURE(1) Sampler2D uSurface;

BIND_IMAGE(0) [[vk::image_format("rg32ui")]] RWTexture2D<uint2> uSeeds;
BIND_IMAGE(1) [[vk::image_format("rg16f")]] RWTexture2D<float2> uField;

static const uint NONE = 0xFFFFFFFFu;
static const float GLOWS_FROM = 0.2;

float2 unpack(uint seed)
{
    return float2(float(seed & 0xFFFFu), float(seed >> 16));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    int2 size = int2(params.uSize);
    if (any(texel >= size)) return;

    int2 cells = int2(params.uCells);
    float2 here = float2(texel);

    // The four cells around this texel, which between them have heard of everything near
    float2 at = (here + 0.5) / float(params.uScale) - 0.5;
    int2 low = int2(floor(at));

    float2 nearest = float2(1e30, 1e30);
    for (int y = 0; y <= 1; y++)
    {
        for (int x = 0; x <= 1; x++)
        {
            int2 cell = clamp(low + int2(x, y), int2(0, 0), cells - 1);
            uint2 seeds = uSeeds[cell];
            if (seeds.x != NONE) { float2 d = unpack(seeds.x) - here; nearest.x = min(nearest.x, dot(d, d)); }
            if (seeds.y != NONE) { float2 d = unpack(seeds.y) - here; nearest.y = min(nearest.y, dot(d, d)); }
        }
    }

    float2 toEdge = float2(
        nearest.x < 1e29 ? sqrt(nearest.x) : params.uFar,
        nearest.y < 1e29 ? sqrt(nearest.y) : params.uFar);

    // The edge of what blocks is half a texel past the texels on it either way
    bool blocks = uMaterial.Load(int3(texel, 0)).g > 0.5;
    bool glows = blocks || uMaterial.Load(int3(texel, 0)).b > GLOWS_FROM;
    float2 distance = float2(
        blocks ? -(toEdge.x + 0.5) : toEdge.x - 0.5,
        glows ? -(toEdge.y + 0.5) : toEdge.y - 0.5) * params.uPixelWorld;

    uField[texel] = distance;
}
