// Turns the occlusion map (a texel per cell, solid or not) into a signed distance field at a few texels per cell,
// in world units, negative inside of a wall. Two passes, the exact euclidean distance transform done one axis at
// a time. The rows pass finds, for every texel, how far the nearest solid texel in its row is (and the nearest
// open one, for the inside). The columns pass takes the smallest over the column of that squared plus the distance
// up or down, which is the real distance. Run once whenever the occlusion map changes, which is rarely.

struct Params
{
    float2 uSize;      // the field, in texels
    float2 uCells;     // the occlusion map, in cells
    float uTexelWorld; // how big a texel of the field is in world units
    int uTexelsPerCell;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// The occlusion map, a byte a cell, over 0.5 where it blocks.
BIND_TEXTURE(0) Sampler2D uOcclusion;

// Between the passes, the row distances to the nearest solid (r) and the nearest open (g) texel, in texels.
BIND_IMAGE(0) [[vk::image_format("rg16f")]] RWTexture2D<float2> uRows;

// What comes out, the signed distance in world units.
BIND_IMAGE(1) [[vk::image_format("r16f")]] RWTexture2D<float> uField;

bool solidAt(int2 texel)
{
    int2 cell = clamp(texel / params.uTexelsPerCell, int2(0, 0), int2(params.uCells) - 1);
    return uOcclusion.Load(int3(cell, 0)).r > 0.5;
}

