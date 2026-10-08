// What every wall throws back, worked out once a frame for the path traced lighting, see gi_cascade.slang. A ray
// that hits a wall used to work out the lamps falling on it there and then, with their shadows, which is the whole
// deferred pass over again for every one of a few hundred thousand rays. Now it is done once for every texel of the
// small picture that is on or next to something that blocks or glows (the rest is never hit, so it is skipped) and
// the rays read it. Its colour times the light that falls on it, from the lamps of its tile with their shadows and
// what the tracer found there last frame (which is how light bounces more than once), and whatever of it glows on top.

#include <lighting/direct.slang>

struct Params
{
    LIGHTING_PARAMS

    float2 uSize;        // the small picture, in pixels
    float uPixelWorld;   // how big a pixel of it is in world units
    float uBounce;       // how much of the light that falls on a wall it throws back
    float2 uShift;       // where this frame's picture was on last frame's, for the bounce
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uTexAlbedo;
BIND_TEXTURE(1) Sampler2D uTexSurface;
BIND_TEXTURE(2) Sampler2D uTexPrevious;

BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uRadiance;

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    if (any(texel >= int2(params.uSize))) return;

    float2 picture = (float2(texel) + 0.5) / params.uSize;
    float2 position = worldOf(picture);
    Field field = LIGHTING_FIELD(params);

    // Only what a ray can land on, which is on or within a couple of pixels of something solid or glowing (the rays
    // read this with a bilinear tap, so the neighbours of a wall count too)
    if (!hasAnything(field) || wallDistance(position, field, true) > 2.0 * params.uPixelWorld)
    {
        uRadiance[texel] = float4(0.0, 0.0, 0.0, 0.0);
        return;
    }

    float3 albedo = uTexAlbedo.SampleLevel(picture, 0.0).rgb;
    float emissive = uTexSurface.SampleLevel(picture, 0.0).b;

    float3 direct = float3(0.0, 0.0, 0.0);
    int tile = tileOf(picture, params.uTileCounts, params.uTileScreen);
    if (tile >= 0 && params.uLightCount > 0)
    {
        bool shadows = params.uShadows != 0 && hasAnything(field);
        uint count = min(lightTiles[tile], TILE_LIGHTS);
        for (uint n = 0; n < count; n++)
        {
            Light l = lights[lightTiles[tile + 1 + int(n)]];
            float2 toLight;
            float falloff;
            direct += l.color * lightReach(l, position, shadows, params.uShadowSoftness, field, toLight, falloff);
        }
    }

    float2 before = picture + params.uShift;
    float3 bounced = all(before >= 0.0) && all(before <= 1.0) ? uTexPrevious.SampleLevel(before, 0.0).rgb : float3(0.0, 0.0, 0.0);

    uRadiance[texel] = float4(albedo * emissive + albedo * (direct + bounced) * params.uBounce, 1.0);
}
