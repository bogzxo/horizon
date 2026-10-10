// The path traced lighting, done as radiance cascades, which is the one trick that makes "every pixel looks in
// every direction" affordable on a GPU that also has a game to draw. The full story is docs/radiance-cascades.md,
// the short one is this.
//
// Light from near by changes fast from point to point (step past a corner and there's a lamp) but needs few
// directions to find. Light from far off needs loads of directions (a far lamp is a sliver of a degree) but
// barely changes as you move. So one grid of probes can't do both and we use a stack of them. Cascade 0 has a
// probe every couple of pixels with four rays each over the first few pixels. Each cascade up has a quarter of the
// probes, four times the rays and an interval four times as long, starting where the last one stopped. A quarter
// times four is one, so every cascade costs the same and six of them cover the whole picture for the price of
// six cascade 0s. Not bad.
//
// A ray only ever stops at a wall (or something that glows, which to a ray is a wall that happens to be lit up
// like a christmas tree). What the wall throws back was worked out once this frame by gi_radiance.slang, the ray
// just reads it. A ray that hits nothing in its interval hands over to the cascade above, whose four narrower rays
// carry on in its cone, so the cascades are built top down and by the time cascade 0 is done each of its rays
// knows about everything out to the edge of the picture. gi_resolve.slang turns cascade 0 into light per pixel.
//
// One texel of a cascade is one ray. The direction is the outer grid and the probe the inner one, so the probes
// of one direction sit next to each other in the texture, which is what lets the merge read them with one
// bilinear sample instead of four loads and a lerp written out by hand.

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
    float uDirectionsSqrt;    // and the square root of that, the rays are laid out in a square of this many a side
    float uIntervalStart;     // how far from the probe a ray starts, in pixels
    float uIntervalEnd;       // and where it gives up
    float2 uUpperProbeCount;  // the same for the cascade above, which this one merges into
    float uUpperSpacing;
    float2 uUpperOffset;
    float uUpperDirectionsSqrt;
    int uHasUpper;
    float uPixelWorld;        // how big a pixel of the picture is in world units
    float uRotation;          // how far every ray is turned this frame, in radians, see PathTracedLighting2D.Turn
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// What the walls throw back (see gi_radiance.slang) and the cascade above.
BIND_TEXTURE(6) Sampler2D uTexRadiance;
BIND_TEXTURE(4) Sampler2D uTexUpper;

// This cascade.
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uCascade;

static const float TAU = 6.28318530718;

// The most steps a ray takes before it gives up. A ray hops by the distance to the nearest wall each time, so it
// only needs this many when it's creeping along a wall, and at that point it isn't going to find anything new
static const int MOST_STEPS = 48;

// One ray, from a probe (in pixels of the picture) in a direction over the cascade's interval. The colour it
// gathered, and in the alpha how much of what is further away still gets through (1 for a ray that hit nothing).
float4 trace(float2 probe, float2 direction, Field field)
{
    // No walls, no sprites, nothing to hit, nothing to do. Everything comes from the cascade above
    if (!hasAnything(field)) return float4(0.0, 0.0, 0.0, 1.0);

    float t = params.uIntervalStart;
    float2 origin = worldOf(probe / params.uSize);
    float2 step = direction * params.uPixelWorld;
    float invPixelWorld = 1.0 / params.uPixelWorld;

    // A probe inside a wall sees out of the face of it, the rays that head deeper in see nothing at all
    float out = 0.0;
    if (!leaveWall(origin, direction, out, field, true))
        return float4(0.0, 0.0, 0.0, 0.0);

    if (out > 0.0)
    {
        out += 0.5 * fieldTexel(field);
        t = max(t, out * invPixelWorld);
    }

    for (int i = 0; i < MOST_STEPS; i++)
    {
        if (t >= params.uIntervalEnd) break;

        float2 position = origin + step * t;
        float toWall = wallDistance(position, field, true);

        if (toWall < 0.25)
        {
            // Landed. Off the picture is nothing, the radiance was only worked out for what's on it
            float2 picture = pictureOfFast(position, field);
            if (any(picture < 0.0) || any(picture > 1.0)) return float4(0.0, 0.0, 0.0, 0.0);
            return float4(uTexRadiance.SampleLevel(picture, 0.0).rgb, 0.0);
        }

        // Sphere tracing, as far as the nearest wall is in one go, never less than half a pixel so it always gets
        // somewhere. A field that says "nothing for forty units" is forty units in one hop
        t += max(toWall * invPixelWorld, 0.5);
    }

    return float4(0.0, 0.0, 0.0, 1.0);
}

// What the cascade above found for a probe position, averaged over the four rays a ray of this cascade splits into
// and over the four probes around the position. The four probes are one bilinear sample each, the hardware does
// the lerp. The sample position is clamped to the probes of this direction cell so it never bleeds into the cell
// next door, which is a different direction entirely and would be the mother of all light leaks.
float4 upper(float2 probePixels, uint direction)
{
    float2 at = (probePixels - params.uUpperOffset) / params.uUpperSpacing - 0.5;
    float2 count = params.uUpperProbeCount;
    at = clamp(at, 0.0, count - 1.0);

    int upperSqrt = int(params.uUpperDirectionsSqrt);
    float2 invCascade = 1.0 / params.uCascadeSize;

    float4 total = float4(0.0, 0.0, 0.0, 0.0);
    for (int k = 0; k < 4; k++)
    {
        int d = int(direction) * 4 + k;
        float2 dirCell = float2(d % upperSqrt, d / upperSqrt);
        total += uTexUpper.SampleLevel((dirCell * count + at + 0.5) * invCascade, 0.0);
    }

    return total * 0.25;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    if (any(texel >= int2(params.uCascadeSize))) return;

    // Which probe and which direction this texel is, see the layout at the top
    int2 count = int2(params.uProbeCount);
    int2 dirCell = texel / count;
    int2 probe = texel - dirCell * count;
    uint direction = uint(dirCell.y * int(params.uDirectionsSqrt) + dirCell.x);

    // The probes are pinned to the world rather than the picture, see PathTracedLighting2D.Offset, or the lamps
    // breathed every time the camera panned a pixel
    float2 probePixels = (float2(probe) + 0.5) * params.uProbeSpacing + params.uProbeOffset;

    // Turned by the same angle in every cascade, so the four rays above that a ray carries on into stay in its cone
    float angle = (float(direction) + 0.5) / params.uDirections * TAU + params.uRotation;
    float2 ray = float2(cos(angle), sin(angle));

    Field field = LIGHTING_FIELD(params);
    float4 found = trace(probePixels, ray, field);

    // What this ray didn't hit, the rays above it carry on with. A ray that hit a wall takes nothing from above,
    // the wall is in the way, and getting that wrong is light coming through walls, which everybody does once
    if (params.uHasUpper != 0 && found.a > 0.0)
    {
        float4 above = upper(probePixels, direction);
        found.rgb += found.a * above.rgb;
        found.a *= above.a;
    }

    uCascade[texel] = found;
}
