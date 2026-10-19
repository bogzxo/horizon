// How bright the picture is, and from that how bright to make it, the way an eye gets used to the dark. See
// ExposureEffect. One group of 256 threads looks at a grid of 64 by 64 points over the picture (the middle counting
// for more than the edges, that is where people look), takes the average of the log of the brightness (so one
// blazing window doesn't make the whole street black), and moves the exposure towards what would bring that average
// to a middle grey, part of the way (uStrength, a night is meant to look like night), no further than the limits,
// over a second or so, quicker going dark than coming back into the light. The one number it keeps lives in a
// buffer the pass after this reads, nothing comes back to the CPU.

struct Params
{
    float uDeltaTime;    // how long the frame was, in seconds
    float uKey;          // the middle grey the average is brought towards, 0.18 the usual
    float uStrength;     // how much of the way there, 0 for none at all and 1 for all the way
    float uLeast;        // the least the exposure may go to, under 1 darkens a bright day
    float uMost;         // and the most, how far into the dark the eye goes
    float uSpeedDark;    // how quickly it gets used to the dark, a share of the way a second
    float uSpeedLight;   // and to the light
    int uReset;          // whether to start again from the target rather than ease towards it (a cut)
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uSource;
BIND_BUFFER(2) RWStructuredBuffer<float> uExposure;

groupshared float sums[256];
groupshared float weights[256];

float luminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

[shader("compute")]
[numthreads(16, 16, 1)]
void csMain(uint3 id : SV_GroupThreadID)
{
    uint me = id.x + id.y * 16u;

    // Four points a thread over the 64 by 64 grid, weighted towards the middle of the picture
    float sum = 0.0, weight = 0.0;
    for (uint j = 0u; j < 4u; j++)
    {
        float2 cell = float2(id.xy * 4u + uint2(j & 1u, j >> 1u) * 2u) + 0.5;
        float2 uv = (cell + 0.5) / 64.0;
        float3 c = uSource.SampleLevel(uv, 0.0).rgb;
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
    float target = clamp(pow(params.uKey / max(average, 1e-4), params.uStrength), params.uLeast, params.uMost);
    float current = uExposure[0];
    if (!(current > 0.0) || params.uReset != 0) current = target;

    // Slowly, an eye that jumps at every lamp that comes into view pumps the whole picture as the camera pans
    float dt = clamp(params.uDeltaTime, 0.0, 0.25);
    float speed = target > current ? params.uSpeedDark : params.uSpeedLight;
    uExposure[0] = lerp(current, target, 1.0 - exp(-dt * speed));
}
