// Bloom, the way up, see BloomEffect. Every level is the one below it blown up with a tent of nine taps, put on
// top of the level of this size from the way down. What comes out of the top is a glow that is wide and soft
// round anything bright.

#include <common/screen.slang>

struct Params
{
    float2 uTexel;   // the size of a pixel of the smaller level, in texture coordinates
    float uRadius;   // how far the taps of the tent reach, in pixels of the smaller level
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// The smaller level blown up, and the level of this size it goes on top of
BIND_TEXTURE(0) Sampler2D uLow;
BIND_TEXTURE(1) Sampler2D uSame;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float2 uv = input.texCoords;
    float2 t = params.uTexel * params.uRadius;

    float3 sum = uLow.Sample(uv).rgb * 4.0;
    sum += (uLow.Sample(uv + float2(-t.x, 0)).rgb + uLow.Sample(uv + float2(t.x, 0)).rgb
          + uLow.Sample(uv + float2(0, -t.y)).rgb + uLow.Sample(uv + float2(0, t.y)).rgb) * 2.0;
    sum += uLow.Sample(uv + float2(-t.x, -t.y)).rgb + uLow.Sample(uv + float2(t.x, -t.y)).rgb
         + uLow.Sample(uv + float2(-t.x, t.y)).rgb + uLow.Sample(uv + float2(t.x, t.y)).rgb;

    return float4(uSame.Sample(uv).rgb + sum / 16.0, 1.0);
}
