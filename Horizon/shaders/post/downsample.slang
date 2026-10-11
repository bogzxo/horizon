// A picture at a quarter of its size, every pixel the average of the four by four it stands for. Four reads, each
// landing where four pixels meet so the GPU averages those. A plain read would take one pixel out of sixteen and
// leave the rest out, which shimmers on anything that moves.

#include <common/screen.slang>

struct Params
{
    // The size of a pixel of the picture that is read, in texture coordinates
    float2 uTexel;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float2 uv = input.texCoords;
    float2 t = params.uTexel;
    return 0.25 * (
        uSource.Sample(uv + float2(-t.x, -t.y)) +
        uSource.Sample(uv + float2(t.x, -t.y)) +
        uSource.Sample(uv + float2(-t.x, t.y)) +
        uSource.Sample(uv + float2(t.x, t.y)));
}
