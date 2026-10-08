// The lighting of a DeferredRenderer2D worked out once per lighting pixel (see DeferredRenderer2D.LightingPixelSize)
// instead of once per pixel of the screen. Pixel art drawn at a few times its size lights the same a few times
// over for every pixel of the art, every screen pixel of it marching the same shadows to the same lights. This
// does it once for every cell of a grid the size of a lighting pixel over what the camera sees, and the deferred
// pass reads the cell its pixel is in, see deferred.slang. The grid is pinned to the world (its origin is a whole
// number of cells), so a cell is the same pixel of the art from frame to frame however the camera moves.

#include <lighting/direct.slang>

struct Params
{
    LIGHTING_PARAMS

    float2 uCellOrigin;   // where the grid starts in the world
    float2 uCellCount;    // cells across and up
    float uPixelSize;     // how big a cell is in world units
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// rg the normal (0.5 being none), b how emissive it is. And r how shiny it is. The G-buffer, at full size
BIND_TEXTURE(1) Sampler2D uTexSurface;
BIND_TEXTURE(2) Sampler2D uTexMaterial;

// What the lights throw straight on the cell, and what shows on top of the surface (the highlights and the glow)
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uLight;
BIND_IMAGE(1) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uExtra;

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 cell = int2(id.xy);
    if (any(cell >= int2(params.uCellCount))) return;

    // The middle of the cell, which is where the deferred pass lights its pixels from as well
    float2 position = params.uCellOrigin + (float2(cell) + 0.5) * params.uPixelSize;
    float2 picture = pictureOf(position);

    float3 light = float3(0.0, 0.0, 0.0), highlights = float3(0.0, 0.0, 0.0), glow = float3(0.0, 0.0, 0.0);
    if (all(picture >= 0.0) && all(picture <= 1.0))
    {
        float4 surface = uTexSurface.SampleLevel(picture, 0.0);
        float shine = uTexMaterial.SampleLevel(picture, 0.0).r * params.uSpecularIntensity;

        bool hasNormal;
        float3 normal = surfaceNormal(surface, hasNormal);

        light = directLight(
            position, picture, normal, hasNormal, shine,
            params.uLightCount, params.uShadows != 0, params.uShininess, params.uShadowSoftness,
            LIGHTING_FIELD(params), params.uTileCounts, params.uTileScreen,
            highlights, glow);
    }

    uLight[cell] = float4(light, 1.0);
    uExtra[cell] = float4(highlights + glow, 1.0);
}
