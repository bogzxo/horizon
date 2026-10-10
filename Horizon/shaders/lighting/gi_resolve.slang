// The last step of the radiance cascades. Every pixel of the light takes the four probes of the nearest cascade
// around it, blends them by how near each is, and averages their rays, which is the light arriving there from every
// direction. That is laid over what the frames before found at the same spot of the world, and goes into the
// deferred pass as the light that isn't straight from a lamp.
//
// Laid over, because one frame on its own is not to be trusted with small lights. A mushroom a few pixels across is
// hit by some rays and missed by the ones next to them, and which ones changes as the camera creeps along, so the
// light round it swapped sides every frame. Every frame goes in for a bit and the rays are turned a little every
// frame (uRotation in gi_cascade.slang), so a few frames together have looked everywhere and the flicker is gone.
// The frames before are read from where the camera has moved them to, the world stands still for that.
//
// Three more things go on in here. The light is kept bigger than it is traced (the renderer's own size, while
// upscaling) and the probes are moved by a fraction of a probe every frame, so a pixel takes most from the frames
// that had a probe close to it (uSharpen) and a few frames fill in detail finer than the probes are apart. Every
// pixel holds on to so many seconds' worth (the alpha, up to uSettle), and a frame of dt seconds goes in by
// dt / (held + dt), so a pixel that has only just come into view is caught up in a few frames rather than fading
// in, and one that holds all it may settles as an average over the last uSettle seconds, whatever the frame rate.
// Seconds and not frames, a pixel that counted frames had its count cut short by every slow frame (a hitch, a
// screenshot) and was under-smoothed for the dozens of frames it took to count back up. And every pixel keeps
// how much its light usually wavers (uMoments, the mean of its brightness and of the square of it), and lets go of
// what it held when the light changes well past that (uResponsiveness), a spark going past is followed and not
// smeared. The usual wavering, the shimmer of the turning rays, is smoothed as before.

struct Params
{
    float2 uSize;           // the light as it is kept, in pixels
    float2 uTraceSize;      // the picture it was traced at
    float2 uProbeCount;     // probes of the nearest cascade
    float uProbeSpacing;
    float2 uProbeOffset;    // where its grid starts against the traced picture, see PathTracedLighting2D.Offset
    float uDirections;
    float2 uShift;          // where this frame's picture was on last frame's, in texture coordinates, off it after a cut
    float uSettle;          // how many seconds' worth a pixel may hold on to, 0 for every frame on its own
    float uFrameTime;       // how long this frame was, in seconds
    float uSharpen;         // 1 while the probes are moved about and a pixel goes by the nearest of them, see above
    float uResponsiveness;  // how readily a pixel lets go of what it held, 0 for never
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uTexCascade;
BIND_TEXTURE(1) Sampler2D uTexHistory;
BIND_TEXTURE(2) Sampler2D uTexMoments;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uResult;
BIND_IMAGE(1) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uMoments;

// How close a probe has to be to count, as a share of the spacing, and what that comes to on average over a cell
// of the grid (the square of the integral of the bell over a unit, worked out once). A pixel with a probe right on
// top of it counts for 1 / 0.55, one in the corner of a cell for a seventh of that, and on average they count for
// one, so the light settles as quickly as it does without upscaling
static const float NEARNESS = 0.35;
static const float NEARNESS_MEAN = 0.552;

// The least share of a frame that goes in. The light is kept in half floats, and less than this rounds away to
// nothing on the way in, the light would stop a few percent short of where it is going and stay there. Over five
// hundred frames a second the light settles quicker than it was told to for it, over fifty frames all the same
static const float LEAST_SHARE = 0.02;

// A change is something that happened when it is past this many times the usual wavering, plus this share of the
// light itself, plus a hair for the dark where nothing wavers at all
static const float WAVERING = 3.0;
static const float SHARE = 0.5;
static const float HAIR = 0.01;

float luminance(float3 colour)
{
    return dot(colour, float3(0.2126, 0.7152, 0.0722));
}

bool finite(float4 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    if (any(texel >= int2(params.uSize))) return;

    // Where the pixel is on the traced picture, in its pixels
    float2 traced = (float2(texel) + 0.5) * params.uTraceSize / params.uSize;

    float2 at = (traced - params.uProbeOffset) / params.uProbeSpacing - 0.5;
    float2 low = floor(at);
    float2 weight = at - low;
    int2 count = int2(params.uProbeCount);
    int sqrtDirections = int(sqrt(params.uDirections) + 0.5);
    int directions = int(params.uDirections);

    float3 total = float3(0.0, 0.0, 0.0);
    for (int y = 0; y < 2; y++)
    {
        for (int x = 0; x < 2; x++)
        {
            int2 probe = clamp(int2(low) + int2(x, y), int2(0, 0), count - 1);
            float w = (x == 0 ? 1.0 - weight.x : weight.x) * (y == 0 ? 1.0 - weight.y : weight.y);

            float3 sum = float3(0.0, 0.0, 0.0);
            for (int d = 0; d < directions; d++)
            {
                int2 dirCell = int2(d % sqrtDirections, d / sqrtDirections);
                sum += uTexCascade.Load(int3(dirCell * count + probe, 0)).rgb;
            }

            total += sum / float(directions) * w;
        }
    }

    // How much this frame counts for here, by how near the nearest probe is
    float2 near = at - round(at);
    float counts = params.uSharpen > 0.0 ? exp(-dot(near, near) / (2.0 * NEARNESS * NEARNESS)) / NEARNESS_MEAN : 1.0;

    float brightness = luminance(total);
    float3 light = total;
    float2 wavering = float2(brightness, brightness * brightness);
    float held = 0.0;

    // What was here last frame, where the picture has moved to. A pixel that has only just come into view has
    // nothing behind it and is this frame alone. And never anything that isn't a number, one bad texel would be
    // carried along from frame to frame for good
    if (params.uSettle > 0.0)
    {
        float2 before = (float2(texel) + 0.5) / params.uSize + params.uShift;
        if (all(before >= 0.0) && all(before <= 1.0))
        {
            float4 history = uTexHistory.SampleLevel(before, 0.0);
            float4 moments = uTexMoments.SampleLevel(before, 0.0);

            if (finite(history) && finite(moments))
            {
                held = history.a;

                // Past what it usually does is something that happened, and what was held goes, but for a frame's
                // worth, a single frame on its own is the shimmer the rest of this is here to be rid of
                if (params.uResponsiveness > 0.0 && held > 0.0)
                {
                    float spread = sqrt(max(moments.y - moments.x * moments.x, 0.0));
                    float allowed = (WAVERING * spread + SHARE * moments.x + HAIR) / params.uResponsiveness;
                    if (abs(brightness - moments.x) > allowed) held = min(held, params.uFrameTime);
                }

                float weighed = params.uFrameTime * counts;
                float share = max(weighed / (held + weighed), LEAST_SHARE * min(counts, 1.0));
                light = lerp(history.rgb, total, share);
                wavering = lerp(moments.xy, wavering, share);
                held = min(held + weighed, params.uSettle);
            }
        }
    }

    uResult[texel] = float4(light, held);
    uMoments[texel] = float4(wavering, 0.0, 1.0);
}
