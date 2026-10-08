// The path traced lighting, done as radiance cascades. Every cascade is a grid of probes over the picture the
// lighting is worked out at, each probe sending rays out in every direction over an interval of distances. The
// nearest cascade has a probe every couple of pixels, four rays each, over the first few pixels. Every cascade up
// has a quarter of the probes, four times the rays and an interval four times as long, so each costs the same and
// a handful of them reach across the whole picture. A cascade is merged with the one above it as it is made, each
// of its rays carrying on into the four rays above that it splits into, averaged over the four nearest probes. The
// result is the light arriving at every probe from every direction, with no noise in it and nothing to smooth over
// frames. See gi_resolve.slang for what the picture makes of the nearest cascade.
//
// What the rays gather is the light that doesn't come straight from a lamp. The lamps themselves are the deferred
// pass's, which lights every pixel by them with shadows (see direct.slang), so a ray only stops at a wall. A wall
// that is hit throws back what gi_radiance.slang worked out for it once this frame, its colour times what falls on
// it, the lamps with their shadows and what the tracer found there last frame (which is how light bounces more than
// once), and whatever of it glows is its own light. A ray marches the signed distance field of the walls to get there.
//
// One texel of a cascade is one ray, laid out with the direction as the outer grid and the probe as the inner one,
// so every cascade is the size of the picture.

#include <lighting/direct.slang>

struct Params
{
    LIGHTING_PARAMS

    float2 uSize;             // the picture, in pixels
    float2 uCascadeSize;      // the cascade texture, probes times rays each way
    float2 uProbeCount;       // probes across and up, this cascade
    float uProbeSpacing;      // pixels between probes, this cascade
    float2 uProbeOffset;      // where the first probe's cell starts against the picture, in pixels, up to a spacing before it
    float uDirections;        // rays per probe, this cascade (a power of four)
    float uIntervalStart;     // how far from the probe a ray starts, in pixels
    float uIntervalEnd;       // and where it gives up
    float2 uUpperProbeCount;  // the same for the cascade above, which this one merges into
    float uUpperSpacing;
    float2 uUpperOffset;
    float uUpperDirections;
    int uHasUpper;
    float uPixelWorld;        // how big a pixel of the picture is in world units
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// What the walls throw back (see gi_radiance.slang) and the cascade above.
BIND_TEXTURE(6) Sampler2D uTexRadiance;
BIND_TEXTURE(4) Sampler2D uTexUpper;

// This cascade.
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uCascade;

static const float TAU = 6.28318530718;

// One ray, from a probe (in pixels of the picture) in a direction over the cascade's interval. The colour it
// gathered, and in the alpha how much of what is further away still gets through (1 for a ray that hit nothing).
float4 trace(float2 probe, float2 direction)
{
    float t = params.uIntervalStart;
    float2 origin = worldOf(probe / params.uSize);
    float2 step = direction * params.uPixelWorld;
    Field field = LIGHTING_FIELD(params);
    bool hasField = hasAnything(field);

    // A probe inside a wall sees out of the face of it, the rays that head deeper in see nothing at all
    if (hasField)
    {
        float out = 0.0;
        if (!leaveWall(origin, direction, out, field, true))
            return float4(0.0, 0.0, 0.0, 0.0);

        if (out > 0.0)
        {
            out += 0.5 * fieldTexel(field);
            t = max(t, out / params.uPixelWorld);
        }
    }

    for (int i = 0; i < 48; i++)
    {
        if (t >= params.uIntervalEnd) break;

        float2 position = origin + step * t;
        float toWall = hasField ? wallDistance(position, field, true) : 1e9;

        if (toWall < 0.25)
        {
            float2 picture = pictureOf(position);
            if (any(picture < 0.0) || any(picture > 1.0)) return float4(0.0, 0.0, 0.0, 0.0);
            return float4(uTexRadiance.SampleLevel(picture, 0.0).rgb, 0.0);
        }

        // As far as the nearest wall is, never less than half a pixel so it always gets somewhere
        t += max(toWall / params.uPixelWorld, 0.5);
    }

    return float4(0.0, 0.0, 0.0, 1.0);
}

// What the cascade above found for a probe position, averaged over the four rays a ray of this cascade splits into
// and over the four probes around the position.
float4 upper(float2 probePixels, uint direction)
{
    float2 at = (probePixels - params.uUpperOffset) / params.uUpperSpacing - 0.5;
    float2 low = floor(at);
    float2 weight = at - low;
    int2 count = int2(params.uUpperProbeCount);
    int upperSqrt = int(sqrt(params.uUpperDirections) + 0.5);

    float4 total = float4(0.0, 0.0, 0.0, 0.0);
    for (int k = 0; k < 4; k++)
    {
        int d = int(direction) * 4 + k;
        int2 dirCell = int2(d % upperSqrt, d / upperSqrt);

        float4 sum = float4(0.0, 0.0, 0.0, 0.0);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int2 probe = clamp(int2(low) + int2(x, y), int2(0, 0), count - 1);
                float w = (x == 0 ? 1.0 - weight.x : weight.x) * (y == 0 ? 1.0 - weight.y : weight.y);
                sum += uTexUpper.Load(int3(dirCell * count + probe, 0)) * w;
            }
        }

        total += sum;
    }

    return total * 0.25;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    if (any(texel >= int2(params.uCascadeSize))) return;

    int2 count = int2(params.uProbeCount);
    int2 dirCell = texel / count;
    int2 probe = texel - dirCell * count;
    int sqrtDirections = int(sqrt(params.uDirections) + 0.5);
    uint direction = uint(dirCell.y * sqrtDirections + dirCell.x);

    // The probes are pinned to the world rather than the picture, see PathTracedLighting2D.Offset
    float2 probePixels = (float2(probe) + 0.5) * params.uProbeSpacing + params.uProbeOffset;
    float angle = (float(direction) + 0.5) / params.uDirections * TAU;
    float2 ray = float2(cos(angle), sin(angle));

    float4 found = trace(probePixels, ray);

    // What this ray didn't hit, the rays above it carry on with
    if (params.uHasUpper != 0 && found.a > 0.0)
    {
        float4 above = upper(probePixels, direction);
        found.rgb += found.a * above.rgb;
        found.a *= above.a;
    }

    uCascade[texel] = found;
}
