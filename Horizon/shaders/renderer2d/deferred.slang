// Lights the G-buffer of a DeferredRenderer2D and puts the result on screen. The lights, what blocks them and the
// shadows live in lighting/direct.slang, shared with the path traced lighting, which hands in what it found through
// uTexGi when it is on.

#include <common/screen.slang>
#include <common/gbuffer.slang>
#include <lighting/direct.slang>

struct Params
{
    LIGHTING_PARAMS

    int uLightingMode;
    float uGiStrength;
    float uGiAmbientScale;

    // The light in the air when the tracer is on. How much of what the tracer found shows in the air itself as a
    // haze (see PathTracedLighting2D.Fog), and the slot in the bindless table of the lamps' glow spread through
    // the air (gi_air.slang), NO_TEXTURE when the lamps glow the plain way
    float uFog;
    uint uAirSlot;

    // The size of the squares of the world that are lit as a whole, 0 for every pixel by itself
    float uPixelSize;
    float3 uAmbient;

    // Whether the lighting was worked out per square already (see light_cells.slang), and where that grid starts
    int uHasCells;
    float2 uCellOrigin;

    // The ambient occlusion, see DeferredRenderer2D.AmbientOcclusion. How far it looks (world units), how many
    // directions, how dark it goes, how much the baked maps count and how much of the direct light it takes too
    int uAoEnabled;
    int uAoSamples;
    float uAoRadius;
    float uAoStrength;
    float uAoBaked;
    float uAoDirect;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// rgb what was drawn, unlit.
BIND_TEXTURE(0) Sampler2D uTexAlbedo;

// rg the normal (0.5 being none), b how emissive it is.
BIND_TEXTURE(1) Sampler2D uTexSurface;

// r how shiny it is, g the baked occlusion under what blocks light, see gbuffer.slang.
BIND_TEXTURE(2) Sampler2D uTexMaterial;

// What the path traced lighting found, see PathTracedLighting2D. Only read when uLightingMode is 1.
BIND_TEXTURE(4) Sampler2D uTexGi;

// The lighting per square, see light_cells.slang. Only read when uHasCells says so.
BIND_TEXTURE(6) Sampler2D uTexCells;
BIND_TEXTURE(7) Sampler2D uTexCellsExtra;

// Every unit of this shader is spoken for, so the air comes in through the bindless table, see uAirSlot
BINDLESS_TEXTURES(uTextures);

// Leaves a colour as it is up to here, and rolls whatever is over off towards 1. Lights can be a lot brighter
// than white without everything they touch turning into a flat patch. It is the colour as a whole that is brought
// down, by its brightest channel, so what is lit too brightly keeps the colour of the light instead of going white.
float3 rollOff(float3 color)
{
    const float knee = 0.8;

    float brightest = max(color.r, max(color.g, color.b));
    if (brightest <= knee) return color;

    float rolled = knee + (1.0 - knee) * (1.0 - exp(-(brightest - knee) / (1.0 - knee)));
    return color * (rolled / brightest);
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float2 texCoords = input.texCoords;
    float3 albedo = uTexAlbedo.Sample(texCoords).rgb;
    float4 surface = uTexSurface.Sample(texCoords);
    float4 material = uTexMaterial.Sample(texCoords);
    float emissive = surface.b;
    float shine = material.r * params.uSpecularIntensity;

    // Where on screen we are is where in the world we are, as far as the camera goes
    float2 position = worldOf(texCoords);

    // Pixel art is lit a pixel of the art at a time, from the middle of it
    if (params.uPixelSize > 0.0) position = (floor(position / params.uPixelSize) + 0.5) * params.uPixelSize;

    float3 light, extra;
    float cellAoWithSprites = 1.0, cellAoWithoutSprites = 1.0;
    bool useCells = params.uPixelSize > 0.0 && params.uHasCells != 0;
    if (useCells)
    {
        // Lit a square at a time already, this is the square the pixel is in
        int2 cell = int2(floor((position - params.uCellOrigin) / params.uPixelSize));
        float4 cellLight = uTexCells.Load(int3(cell, 0));
        float4 cellExtra = uTexCellsExtra.Load(int3(cell, 0));
        light = cellLight.rgb;
        extra = cellExtra.rgb;
        cellAoWithSprites = cellLight.a;
        cellAoWithoutSprites = cellExtra.a;
    }
    else
    {
        bool hasNormal;
        float3 normal = surfaceNormal(surface, hasNormal);

        float3 highlights, glow;
        light = directLight(
            position, texCoords, normal, hasNormal, shine,
            params.uLightCount, params.uShadows != 0, params.uShininess, params.uShadowSoftness, params.uGlowScale,
            LIGHTING_FIELD(params), params.uTileCounts, params.uTileScreen,
            highlights, glow);
        extra = highlights + glow;
    }

    // For looking at what the tracer found on its own, see DeferredRenderer2D.ShowTracedLight
    if (params.uLightingMode == 2)
        return float4(uTexGi.Sample(texCoords).rgb, 1.0);

    // How much of the light that comes from everywhere gets here, out of the field round the point and out of what
    // the artist painted into the corners of the tiles
    float occlusion = 1.0;
    if (params.uAoEnabled != 0)
    {
        // Above a half the green is a sprite saying it blocks light, see gbuffer.slang
        float green = material.g;
        float traced = useCells
            ? (green <= 0.5 ? cellAoWithSprites : cellAoWithoutSprites)
            : ambientOcclusion(position, LIGHTING_FIELD(params), params.uAoRadius, params.uAoSamples, params.uAoStrength, green <= 0.5);
        float baked = unpackOcclusion(green);
        occlusion = traced * lerp(1.0, baked, params.uAoBaked);
    }

    // For looking at the occlusion on its own, see AmbientOcclusion2D.Show
    if (params.uLightingMode == 3)
        return float4(occlusion, occlusion, occlusion, 1.0);

    // The lights themselves only take as much of it as asked, a lamp shines into a corner all the same
    light *= lerp(1.0, occlusion, params.uAoDirect);

    if (params.uLightingMode == 1)
    {
        // Bounced light and what the lamps throw as things with a size, gathered by the cascades. The ambient makes
        // way for it, the dark corners get their light round the corner now instead of out of nowhere
        float3 traced = uTexGi.Sample(texCoords).rgb;
        light += (params.uAmbient * params.uGiAmbientScale + traced * params.uGiStrength) * occlusion;

        // And the air. Some of the light at a point shows in the air at that point whatever is under it, a haze,
        // and the lamps' glow comes spread through the air by the tracer. Neither is multiplied by the surface,
        // they hang in front of it
        extra += traced * params.uFog;
        if (params.uAirSlot != NO_TEXTURE)
            extra += uTextures[NonUniformResourceIndex(params.uAirSlot)].Sample(texCoords).rgb;
    }
    else
    {
        light += params.uAmbient * occlusion;
    }

    // What is emissive shows as it was drawn no matter the light
    float3 color = albedo * lerp(light, float3(1.0, 1.0, 1.0), emissive) + extra;

    return float4(rollOff(color), 1.0);
}
