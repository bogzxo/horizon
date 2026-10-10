// The last step of the radiance cascades. Every pixel of the small picture takes the four probes of the nearest
// cascade around it, blends them by how near each is, and averages their rays, which is the light arriving there
// from every direction. That is laid over what the frames before found at the same spot of the world, and goes
// into the deferred pass as the light that isn't straight from a lamp.
//
// Laid over, because one frame on its own is not to be trusted with small lights. A mushroom a few pixels across is
// hit by some rays and missed by the ones next to them, and which ones changes as the camera creeps along, so the
// light round it swapped sides every frame. Every frame goes in for a bit (uBlend, see PathTracedLighting2D.Blend)
// and the rays are turned a little every frame (uRotation in gi_cascade.slang), so a few frames together have
// looked everywhere and the flicker is gone. The frames before are read from where the camera has moved them to,
// the world stands still for that, what moves by itself leaves a short tail of its light behind it.

struct Params
{
    float2 uSize;        // the small picture, in pixels
    float2 uProbeCount;  // probes of the nearest cascade
    float uProbeSpacing;
    float2 uProbeOffset; // where its grid starts against the picture, see PathTracedLighting2D.Offset
    float uDirections;
    float2 uShift;       // where this frame's picture was on last frame's, in texture coordinates, off it after a cut
    float uBlend;        // how much of this frame goes in, 1 for all of it and none of the frames before
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uTexCascade;
BIND_TEXTURE(1) Sampler2D uTexHistory;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uResult;

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    if (any(texel >= int2(params.uSize))) return;

    float2 at = (float2(texel) + 0.5 - params.uProbeOffset) / params.uProbeSpacing - 0.5;
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

    // What was here last frame, where the picture has moved to. A pixel that has only just come into view has
    // nothing behind it and is this frame alone. And never anything that isn't a number, one bad texel would be
    // carried along from frame to frame for good
    if (params.uBlend < 1.0)
    {
        float2 before = (float2(texel) + 0.5) / params.uSize + params.uShift;
        if (all(before >= 0.0) && all(before <= 1.0))
        {
            float3 history = uTexHistory.SampleLevel(before, 0.0).rgb;
            if (!any(isnan(history)) && !any(isinf(history)))
                total = lerp(history, total, params.uBlend);
        }
    }

    uResult[texel] = float4(total, 1.0);
}
