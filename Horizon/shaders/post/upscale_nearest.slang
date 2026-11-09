// A scene drawn small blown up to the window with every window pixel taking the one scene pixel its middle lands
// in, for pixel art that wants hard edges. Read by its texel rather than through a sampler, so it is the same on every
// GPU whatever the texture's own filter is. See Horizon.Rendering.Scaling.FrameScaler.

#include <common/screen.slang>

struct Params
{
    // Scene pixels a window pixel each way
    float2 uScale;

    // How big the scene is
    float2 uSceneSize;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    int2 texel = int2(floor(input.position.xy * params.uScale));
    texel = clamp(texel, int2(0, 0), int2(params.uSceneSize) - int2(1, 1));
    return uSource.Load(int3(texel, 0));
}
