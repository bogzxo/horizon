// One pass of a gaussian blur, along whichever way uStep points. Run it twice (sideways, then up and down) for a
// blur in every direction. Nine taps of the bell curve for the price of five reads, by reading between two pixels
// and letting the GPU blend them.

#include <common/screen.slang>

struct Params
{
    // How far apart the reads are, in texture coordinates. Zero in one of the two is what makes this one way only
    float2 uStep;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float2 uv = input.texCoords;
    float4 sum = uSource.Sample(uv) * 0.2270270270;

    sum += uSource.Sample(uv + params.uStep * 1.3846153846) * 0.3162162162;
    sum += uSource.Sample(uv - params.uStep * 1.3846153846) * 0.3162162162;
    sum += uSource.Sample(uv + params.uStep * 3.2307692308) * 0.0702702703;
    sum += uSource.Sample(uv - params.uStep * 3.2307692308) * 0.0702702703;

    // The alpha is blurred along with the colours, so this works on a see-through layer as well
    return sum;
}
