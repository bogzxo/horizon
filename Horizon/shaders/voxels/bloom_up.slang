// Bloom, the way up. Every level is the one below it blown up with a tent of nine taps, put on top of the level of
// this size from the way down. What comes out of the top is a glow that is wide and soft round anything bright.

#include <voxels/frame.slang>

struct Params
{
    float2 uTargetSize;
    float2 uLowSize;
    float uRadius;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uLow;
BIND_TEXTURE(1) Sampler2D uSame;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uOut;

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    if (any(float2(id.xy) >= params.uTargetSize)) return;

    float2 uv = (float2(id.xy) + 0.5) / params.uTargetSize;
    float2 t = params.uRadius / params.uLowSize;

    float3 sum = uLow.SampleLevel(uv, 0.0).rgb * 4.0;
    sum += (uLow.SampleLevel(uv + float2(-t.x, 0), 0.0).rgb + uLow.SampleLevel(uv + float2(t.x, 0), 0.0).rgb
          + uLow.SampleLevel(uv + float2(0, -t.y), 0.0).rgb + uLow.SampleLevel(uv + float2(0, t.y), 0.0).rgb) * 2.0;
    sum += uLow.SampleLevel(uv + float2(-t.x, -t.y), 0.0).rgb + uLow.SampleLevel(uv + float2(t.x, -t.y), 0.0).rgb
         + uLow.SampleLevel(uv + float2(-t.x, t.y), 0.0).rgb + uLow.SampleLevel(uv + float2(t.x, t.y), 0.0).rgb;

    uOut[id.xy] = float4(uSame.Load(int3(int2(id.xy), 0)).rgb + sum / 16.0, 1.0);
}
