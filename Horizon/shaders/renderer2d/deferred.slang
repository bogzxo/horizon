// Lights the G-buffer of a DeferredRenderer2D and puts the result on screen. The lights, what blocks them and the
// shadows live in lighting/direct.slang, shared with the path traced lighting, which hands in what it found through
// uTexGi when it is on.

#include <common/screen.slang>
#include <lighting/direct.slang>

struct Params
{
    LIGHTING_PARAMS

    int uLightingMode;
    float uGiStrength;
    float uGiAmbientScale;

    // The size of the squares of the world that are lit as a whole, 0 for every pixel by itself
    float uPixelSize;
    float3 uAmbient;

    // Whether the lighting was worked out per square already (see light_cells.slang), and where that grid starts
    int uHasCells;
    float2 uCellOrigin;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// rgb what was drawn, unlit.
BIND_TEXTURE(0) Sampler2D uTexAlbedo;

// rg the normal (0.5 being none), b how emissive it is.
BIND_TEXTURE(1) Sampler2D uTexSurface;

// r how shiny it is.
BIND_TEXTURE(2) Sampler2D uTexMaterial;

// What the path traced lighting found, see PathTracedLighting2D. Only read when uLightingMode is 1.
BIND_TEXTURE(4) Sampler2D uTexGi;

// The lighting per square, see light_cells.slang. Only read when uHasCells says so.
BIND_TEXTURE(6) Sampler2D uTexCells;
BIND_TEXTURE(7) Sampler2D uTexCellsExtra;

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
    float emissive = surface.b;
    float shine = uTexMaterial.Sample(texCoords).r * params.uSpecularIntensity;

    // Where on screen we are is where in the world we are, as far as the camera goes
    float2 position = worldOf(texCoords);

    // Pixel art is lit a pixel of the art at a time, from the middle of it
    if (params.uPixelSize > 0.0) position = (floor(position / params.uPixelSize) + 0.5) * params.uPixelSize;

    float3 light, extra;
    if (params.uPixelSize > 0.0 && params.uHasCells != 0)
    {
        // Lit a square at a time already, this is the square the pixel is in
        int2 cell = int2(floor((position - params.uCellOrigin) / params.uPixelSize));
        light = uTexCells.Load(int3(cell, 0)).rgb;
        extra = uTexCellsExtra.Load(int3(cell, 0)).rgb;
    }
    else
    {
        bool hasNormal;
        float3 normal = surfaceNormal(surface, hasNormal);

        float3 highlights, glow;
        light = directLight(
            position, texCoords, normal, hasNormal, shine,
            params.uLightCount, params.uShadows != 0, params.uShininess, params.uShadowSoftness,
            LIGHTING_FIELD(params), params.uTileCounts, params.uTileScreen,
            highlights, glow);
        extra = highlights + glow;
    }

    // For looking at what the tracer found on its own, see DeferredRenderer2D.ShowTracedLight
    if (params.uLightingMode == 2)
        return float4(uTexGi.Sample(texCoords).rgb, 1.0);

    if (params.uLightingMode == 1)
    {
        // Bounced light and what the lamps throw as things with a size, gathered by the cascades. The ambient makes
        // way for it, the dark corners get their light round the corner now instead of out of nowhere
        light += params.uAmbient * params.uGiAmbientScale + uTexGi.Sample(texCoords).rgb * params.uGiStrength;
    }
    else
    {
        light += params.uAmbient;
    }

    // What is emissive shows as it was drawn no matter the light
    float3 color = albedo * lerp(light, float3(1.0, 1.0, 1.0), emissive) + extra;

    return float4(rollOff(color), 1.0);
}
