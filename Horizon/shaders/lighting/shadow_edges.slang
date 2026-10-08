// The first pass of the sprite shadows. Everything that was drawn as blocking light (see Sprite.CastsShadows) left
// its coverage in the green of the material attachment, and everything that glows its emissive in the blue of the
// surface attachment. This notes, for every texel, whether it blocks, whether it blocks or glows, and whether it is
// on the edge of either (a texel that does with a neighbour that doesn't). The two passes after this one
// (shadow_columns.slang and shadow_rows.slang) turn the edges into distances.

struct Params
{
    float2 uSize;   // the picture, in texels
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uMaterial;
BIND_TEXTURE(1) Sampler2D uSurface;

// r blocks, g blocks or glows, b on the edge of what blocks, a on the edge of what blocks or glows. 1 for yes.
BIND_IMAGE(0) [[vk::image_format("rgba8")]] RWTexture2D<float4> uEdges;

static const float GLOWS_FROM = 0.2;

float2 at(int2 texel, int2 size)
{
    texel = clamp(texel, int2(0, 0), size - 1);
    bool blocks = uMaterial.Load(int3(texel, 0)).g > 0.5;
    bool glows = blocks || uSurface.Load(int3(texel, 0)).b > GLOWS_FROM;
    return float2(blocks ? 1.0 : 0.0, glows ? 1.0 : 0.0);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 size = int2(params.uSize);
    int2 texel = int2(id.xy);
    if (any(texel >= size)) return;

    float2 here = at(texel, size);
    float2 around = min(min(at(texel + int2(1, 0), size), at(texel - int2(1, 0), size)), min(at(texel + int2(0, 1), size), at(texel - int2(0, 1), size)));

    // On an edge if this one does and any of the four around it doesn't
    float2 edge = here * (1.0 - around);
    uEdges[texel] = float4(here, edge);
}
