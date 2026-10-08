// Two pictures blended into one. At an amount of 0 it is all the first one, at 1 all the other.
// This is what lets a blur come on without a pop, and what crossfades one scene into the next.

#include <common/screen.slang>

struct Params
{
    float uAmount;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;
BIND_TEXTURE(1) Sampler2D uOther;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    return lerp(uSource.Sample(input.texCoords), uOther.Sample(input.texCoords), params.uAmount);
}
