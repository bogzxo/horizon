// Bloom, laid over the picture, see BloomEffect. The glow comes in at half the size of the picture and is read
// between its pixels, which is a blur of its own on top of the ones it had.

#include <common/screen.slang>

struct Params
{
    float uIntensity;   // how much of the glow goes over the picture
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uSource;
BIND_TEXTURE(1) Sampler2D uBloom;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float4 source = uSource.Sample(input.texCoords);
    float3 glow = uBloom.Sample(input.texCoords).rgb * params.uIntensity;
    return float4(source.rgb + glow, source.a);
}
