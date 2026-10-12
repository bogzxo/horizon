// The light in the air, for the path traced lighting. A lamp's glow (Light2D.Glow, how much of it hangs in the
// air round it) used to be worked out by the deferred pass at every pixel of the screen, shadowed, and put down
// as it was, a hard edged halo. This works it out once a frame for every texel of the tracer's small picture,
// shadowed the same way (a wall stops it, a doorway lets a shaft of it through), and the tracer spreads it by
// PathTracedLighting2D.GlowSpread before the deferred pass lays it over the picture, so it hangs in the air the
// way a haze under a lamp does, soft, round corners a little, and stopped by the walls. The light the tracer
// bounces is added to the air as well, by its Fog, in the deferred pass.

#include <lighting/direct.slang>

struct Params
{
    LIGHTING_PARAMS

    float2 uSize;   // the small picture, in pixels
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uAir;

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    if (any(texel >= int2(params.uSize))) return;

    float2 picture = (float2(texel) + 0.5) / params.uSize;
    float2 position = worldOf(picture);
    Field field = LIGHTING_FIELD(params);

    float3 air = float3(0.0, 0.0, 0.0);
    int tile = tileOf(picture, params.uTileCounts, params.uTileScreen);
    if (tile >= 0 && params.uLightCount > 0)
    {
        bool shadows = params.uShadows != 0 && hasAnything(field);
        uint count = min(lightTiles[tile], TILE_LIGHTS);
        for (uint n = 0; n < count; n++)
        {
            Light l = lights[lightTiles[tile + 1 + int(n)]];
            if (l.glow <= 0.0) continue;

            float2 toLight;
            float falloff;
            float reach = lightReach(l, position, shadows, params.uShadowSoftness, field, toLight, falloff);
            air += l.color * (reach * falloff * l.glow);
        }
    }

    uAir[texel] = float4(air, 1.0);
}
