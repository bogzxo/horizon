// What the window shows before there is a scene to show, a colour with a picture in the middle of it. See
// LoadingScreen. The picture is laid over the colour by its alpha, so a logo with a see-through background sits on
// whatever colour was asked for.

#include <common/screen.slang>

struct Params
{
    float4 uColor;

    // where the picture goes, its top left corner and its size, in the 0 to 1 of the screen. No size, no picture
    float4 uPlace;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float3 color = params.uColor.rgb;

    if (params.uPlace.z > 0.0 && params.uPlace.w > 0.0)
    {
        float2 at = (input.texCoords - params.uPlace.xy) / params.uPlace.zw;
        if (all(at >= 0.0) && all(at <= 1.0))
        {
            // the level is said outright, there are no derivatives to be had inside of a branch
            float4 picture = uSource.SampleLevel(at, 0.0);
            color = lerp(color, picture.rgb, picture.a);
        }
    }

    return float4(color, 1.0);
}
