// A flat colour over everything, for covering a scene up while it is swapped for another one. See FadeTransition.
// How much of what is under it still shows is up to the alpha of the colour, this is drawn blended.

#include <common/screen.slang>

struct Params
{
    float4 uColor;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    return params.uColor;
}
