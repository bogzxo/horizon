// Bloom, the way down. Every level is half the size of the one before, made of thirteen taps of it laid out so
// nothing is missed and nothing shimmers (the Call of Duty one, Jimenez 2014). The first level keeps only what is
// brighter than the threshold (softly, no hard edge) and weighs every group of taps by how bright it is, so one
// blazing pixel of sun on the water doesn't make a square of light that flickers.

#include <voxels/frame.slang>

struct Params
{
    float2 uTargetSize;
    float uThreshold;
    int uFirst;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uSource;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uOut;

float3 karis(float3 a, float3 b, float3 c, float3 d)
{
    float wa = 1.0 / (1.0 + luminance(a)), wb = 1.0 / (1.0 + luminance(b));
    float wc = 1.0 / (1.0 + luminance(c)), wd = 1.0 / (1.0 + luminance(d));
    return (a * wa + b * wb + c * wc + d * wd) / (wa + wb + wc + wd);
}

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    if (any(float2(id.xy) >= params.uTargetSize)) return;

    float2 uv = (float2(id.xy) + 0.5) / params.uTargetSize;
    float2 t = 1.0 / (params.uTargetSize * 2.0);

    float3 a = uSource.SampleLevel(uv + t * float2(-2, -2), 0.0).rgb;
    float3 b = uSource.SampleLevel(uv + t * float2(0, -2), 0.0).rgb;
    float3 c = uSource.SampleLevel(uv + t * float2(2, -2), 0.0).rgb;
    float3 d = uSource.SampleLevel(uv + t * float2(-2, 0), 0.0).rgb;
    float3 e = uSource.SampleLevel(uv, 0.0).rgb;
    float3 g = uSource.SampleLevel(uv + t * float2(2, 0), 0.0).rgb;
    float3 h = uSource.SampleLevel(uv + t * float2(-2, 2), 0.0).rgb;
    float3 i = uSource.SampleLevel(uv + t * float2(0, 2), 0.0).rgb;
    float3 j = uSource.SampleLevel(uv + t * float2(2, 2), 0.0).rgb;
    float3 k = uSource.SampleLevel(uv + t * float2(-1, -1), 0.0).rgb;
    float3 l = uSource.SampleLevel(uv + t * float2(1, -1), 0.0).rgb;
    float3 m = uSource.SampleLevel(uv + t * float2(-1, 1), 0.0).rgb;
    float3 n = uSource.SampleLevel(uv + t * float2(1, 1), 0.0).rgb;

    float3 color;
    if (params.uFirst != 0)
    {
        color = karis(k, l, m, n) * 0.5
            + karis(a, b, d, e) * 0.125 + karis(b, c, e, g) * 0.125
            + karis(d, e, h, i) * 0.125 + karis(e, g, i, j) * 0.125;

        // Only what is over the threshold, with a soft knee
        float brightness = max(color.r, max(color.g, color.b));
        float knee = params.uThreshold * 0.5;
        float soft = clamp(brightness - params.uThreshold + knee, 0.0, 2.0 * knee);
        soft = soft * soft / (4.0 * knee + 1e-4);
        float kept = max(soft, brightness - params.uThreshold) / max(brightness, 1e-4);
        color *= kept;
    }
    else
    {
        color = (k + l + m + n) * 0.125 + (a + c + h + j) * 0.03125 + (b + d + g + i) * 0.0625 + e * 0.125;
    }

    uOut[id.xy] = float4(color, 1.0);
}
