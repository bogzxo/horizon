// How bright the picture is made, worked out from the picture, the way an eye gets used to the dark. One group of 256
// threads looks at a grid of the frame (the middle counting for more than the edges, that's where people look), takes
// the average of the log of the brightness (so one blazing lamp doesn't make the whole cave black), and moves the
// exposure towards what would make that average a middle grey, over a few seconds, quicker going dark than coming
// back to the light. Only part of the way there, a night is meant to look like night and a cave like a cave, it just
// shouldn't be a black screen.

#include <voxels/frame.slang>

BIND_TEXTURE(0) Sampler2D uScene;
BIND_BUFFER(7) RWStructuredBuffer<float> uExposure;

groupshared float sums[256];
groupshared float weights[256];

[numthreads(16, 16, 1)]
void csMain(uint3 id : SV_GroupThreadID)
{
    FrameData f = frame();
    uint me = id.x + id.y * 16u;

    // Four points a thread, 64 x 64 of the frame
    float sum = 0.0, weight = 0.0;
    for (uint j = 0u; j < 4u; j++)
    {
        float2 cell = float2(id.xy * 4u + uint2(j & 1u, j >> 1u) * 2u) + 0.5;
        float2 uv = (cell + 0.5) / 64.0;
        float3 c = uScene.SampleLevel(uv, 0.0).rgb;
        float2 centered = uv - 0.5;
        float w = 1.0 - saturate(dot(centered, centered) * 2.2);
        sum += log2(luminance(c) + 1e-4) * w;
        weight += w;
    }

    sums[me] = sum;
    weights[me] = weight;
    GroupMemoryBarrierWithGroupSync();

    for (uint stride = 128u; stride > 0u; stride >>= 1u)
    {
        if (me < stride)
        {
            sums[me] += sums[me + stride];
            weights[me] += weights[me + stride];
        }

        GroupMemoryBarrierWithGroupSync();
    }

    if (me != 0u) return;

    float average = exp2(sums[0] / max(weights[0], 1e-4));

    // Part of the way to a middle grey, no further than the limits
    float target = clamp(pow(0.22 / max(average, 1e-4), 0.5), 0.6, 6.0);
    float current = uExposure[0];
    if (!(current > 0.0) || f.misc.w > 0.5) current = target;

    float dt = clamp(f.history.w, 0.0, 0.25);
    // Slowly, an eye that jumps at every look at the sky pumps the whole picture as the camera pans
    float speed = target < current ? 0.9 : 0.5;
    uExposure[0] = lerp(current, target, 1.0 - exp(-dt * speed));
}
