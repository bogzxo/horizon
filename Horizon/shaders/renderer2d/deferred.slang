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

// How open to the ambient light a point of the world is, 1 for wide open, down to 1 minus the strength in a corner.
// Ambient light comes from everywhere, so what is hemmed in by walls and sprites gets less of it, the corner where
// the floor meets a wall, the ground under a fighter's feet, the air right next to a pillar. A few rays are
// marched a short way through the distance field, every one that meets something within the radius takes a share
// of the light, the sooner it meets it the bigger the share. A point inside a wall is its face, the rays start
// just outside and only go the way the face looks, a flat floor is wide open and its corners are not, and the
// deeper in the point is the less of that it takes, so what a foot or a crate does to the floor fades out on the
// way down instead of standing in it as a bar. Only the walls say where the face is, a sprite is a picture in front
// of the world and has no inside to speak of, and a pixel that is such a sprite doesn't count the sprites at all
// (sprites false), or every fighter is hemmed in by themselves and comes out speckled like a fucking dalmatian.
float ambientOcclusion(float2 position, Field field, float radius, int samples, float strength, bool sprites)
{
    if (!hasAnything(field) || samples <= 0 || radius <= 0.0 || strength <= 0.0) return 1.0;

    float texel = fieldTexel(field);
    float2 at = position;
    float from = 0.0, span = 6.2831853;
    float share = 1.0;

    float d = wallsDistance(at, field);
    if (d < texel)
    {
        if (-d >= radius) return 1.0;

        float2 slope = float2(
            wallsDistance(at + float2(texel, 0.0), field) - wallsDistance(at - float2(texel, 0.0), field),
            wallsDistance(at + float2(0.0, texel), field) - wallsDistance(at - float2(0.0, texel), field));
        if (dot(slope, slope) < 1e-8) return 1.0;

        float2 outward = normalize(slope);
        if (d < 0.0)
        {
            at += outward * (-d + 0.5 * texel);

            // Next to a ridge of the field (under a crate that stands on the floor, the middle of a box) the slope
            // points somewhere near the face and not at it, and whether that lands in the open or not changes from
            // one pixel to the next, which is noise. Once more from where it landed and it is out
            float still = wallsDistance(at, field);
            if (still < 0.0)
            {
                at += outward * (-still + 0.5 * texel);

                // Still in, which is the ridge itself, there is no face to find from here and it is left open
                if (wallsDistance(at, field) < 0.0) return 1.0;
            }

            share = 1.0 - clamp(-d / radius, 0.0, 1.0);
        }

        // The half of the directions the face looks towards
        from = atan2(outward.y, outward.x) - 1.5707963;
        span = 3.1415927;
    }

    float occluded = 0.0;
    for (int i = 0; i < samples; i++)
    {
        float angle = from + (float(i) + 0.5) * span / float(samples);
        float2 direction = float2(cos(angle), sin(angle));

        float t = 0.5 * texel;
        float hit = radius;
        for (int step = 0; step < 4; step++)
        {
            float2 ahead = at + direction * t;
            float distance = sprites ? wallDistance(ahead, field) : wallsDistance(ahead, field);
            if (distance < 0.05)
            {
                hit = t;
                break;
            }

            t += max(distance, 0.5 * texel);
            if (t >= radius) break;
        }

        occluded += 1.0 - hit / radius;
    }

    return 1.0 - strength * share * occluded / float(samples);
}

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

    // How much of the light that comes from everywhere gets here, out of the field round the point and out of what
    // the artist painted into the corners of the tiles
    float occlusion = 1.0;
    if (params.uAoEnabled != 0)
    {
        // Above a half the green is a sprite saying it blocks light, see gbuffer.slang
        float green = uTexMaterial.Sample(texCoords).g;
        float traced = ambientOcclusion(position, LIGHTING_FIELD(params), params.uAoRadius, params.uAoSamples, params.uAoStrength, green <= 0.5);
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
        light += (params.uAmbient * params.uGiAmbientScale + uTexGi.Sample(texCoords).rgb * params.uGiStrength) * occlusion;
    }
    else
    {
        light += params.uAmbient * occlusion;
    }

    // What is emissive shows as it was drawn no matter the light
    float3 color = albedo * lerp(light, float3(1.0, 1.0, 1.0), emissive) + extra;

    return float4(rollOff(color), 1.0);
}
