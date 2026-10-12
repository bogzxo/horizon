// Bloom, the way down, see BloomEffect. Every level is half the size of the one before, made of thirteen taps of
// it laid out so nothing is missed and nothing shimmers (the Call of Duty one, Jimenez 2014). The first level
// keeps only what is brighter than the threshold (softly, no hard edge) and weighs every group of taps by how
// bright it is, so one blazing pixel doesn't make a square of light that flickers. The same as the voxel
// renderer's bloom_down, as a fragment pass over a post target.

#include <common/screen.slang>

struct Params
{
    float2 uTexel;       // the size of a pixel of the picture that is read, in texture coordinates
    float uThreshold;    // what is darker than this is left out, on the first level
    int uFirst;          // whether this is the first level, the one that thresholds
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

float luminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float3 karis(float3 a, float3 b, float3 c, float3 d)
{
    float wa = 1.0 / (1.0 + luminance(a)), wb = 1.0 / (1.0 + luminance(b));
    float wc = 1.0 / (1.0 + luminance(c)), wd = 1.0 / (1.0 + luminance(d));
    return (a * wa + b * wb + c * wc + d * wd) / (wa + wb + wc + wd);
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float2 uv = input.texCoords;
    float2 t = params.uTexel;

    float3 a = uSource.Sample(uv + t * float2(-2, -2)).rgb;
    float3 b = uSource.Sample(uv + t * float2(0, -2)).rgb;
    float3 c = uSource.Sample(uv + t * float2(2, -2)).rgb;
    float3 d = uSource.Sample(uv + t * float2(-2, 0)).rgb;
    float3 e = uSource.Sample(uv).rgb;
    float3 g = uSource.Sample(uv + t * float2(2, 0)).rgb;
    float3 h = uSource.Sample(uv + t * float2(-2, 2)).rgb;
    float3 i = uSource.Sample(uv + t * float2(0, 2)).rgb;
    float3 j = uSource.Sample(uv + t * float2(2, 2)).rgb;
    float3 k = uSource.Sample(uv + t * float2(-1, -1)).rgb;
    float3 l = uSource.Sample(uv + t * float2(1, -1)).rgb;
    float3 m = uSource.Sample(uv + t * float2(-1, 1)).rgb;
    float3 n = uSource.Sample(uv + t * float2(1, 1)).rgb;

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

    return float4(color, 1.0);
}
