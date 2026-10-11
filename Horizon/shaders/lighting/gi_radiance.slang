// What every wall throws back, worked out once a frame for the path tracer. A ray that lands on a wall needs to
// know how bright that bit of wall is, which is its colour times the light falling on it, lamps with shadows and
// all. Working that out at the end of every ray is the whole deferred pass over again a few hundred thousand times
// a frame, which is how it was done for about a day before anybody looked at the frame time. Now it's done once for
// every texel of the small picture that is on or next to something solid or glowing (the rest can't be hit, so it's
// skipped) and the rays read the answer.
//
// The light falling on a wall includes what the tracer found there last frame, which is how light bounces more
// than once. Every frame carries last frame's bounce one step further, so a torch lights a wall, the wall lights
// the wall opposite, and by the fourth frame or so it's lit the ceiling. uBounce is how much a wall keeps, under 1
// or it feeds on itself and the room goes nuclear.

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
BIND_TEXTURE(1) Sampler2D uTexMaterial;
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
    // read this with a bilinear tap, so the neighbours of a wall count too). Most of a picture is air, and air
    // costs one distance lookup and a write of nothing
    if (!hasAnything(field) || wallDistance(position, field, true) > 2.0 * params.uPixelWorld)
    {
        uRadiance[texel] = float4(0.0, 0.0, 0.0, 0.0);
        return;
    }

    float3 albedo = uTexAlbedo.SampleLevel(picture, 0.0).rgb;

    // How much of it is a lamp. The blue of the material, which is "lights things", not the surface's "shows
    // unlit", a sprite flashed white on a hit shows bright but doesn't light the room, see gbuffer.slang
    float emissive = uTexMaterial.SampleLevel(picture, 0.0).b;

    // The lamps of this tile, with their shadows, exactly as the deferred pass would light this spot
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

    // And what the tracer found here last frame, read from where the camera has moved it to. Off the picture
    // after a cut, which is nothing, and the bounce starts again from the lamps
    float2 before = picture + params.uShift;
    float3 bounced = all(before >= 0.0) && all(before <= 1.0) ? uTexPrevious.SampleLevel(before, 0.0).rgb : float3(0.0, 0.0, 0.0);

    uRadiance[texel] = float4(albedo * emissive + albedo * (direct + bounced) * params.uBounce, 1.0);
}
