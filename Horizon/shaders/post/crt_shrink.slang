// The first pass of the CRT effect. The picture at the size the tube shows it at, one pixel for every dot of a
// scan line. It is drawn into a texture that hands its colours back in linear light (see PostTarget.LinearLight),
// which is where the second pass does its sums.

#include <common/screen.slang>

struct Params
{
    // How far from the middle of a pixel of the result the four reads are, in the picture's own coordinates. Each
    // read is blended between the pixels around it, so between the four of them they cover the whole of what one
    // pixel of the result stands for. Nothing is skipped over when the picture is a lot bigger than the tube
    float2 uSpread;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uSource;

// The picture comes in as it is shown, sRGB. The texture this draws into is an sRGB one too, and Vulkan encodes
// whatever is written to one of those, so what goes out of here has to be linear light or the picture gets its
// gamma twice over and comes out washed out. What the tube reads back is then linear light, as it wants it.
float3 toLinear(float3 colour)
{
    colour = max(colour, float3(0.0, 0.0, 0.0));
    return lerp(colour / 12.92, pow((colour + 0.055) / 1.055, float3(2.4, 2.4, 2.4)), step(float3(0.04045, 0.04045, 0.04045), colour));
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    float2 uv = input.texCoords;
    float4 shown = 0.25 * (
        uSource.Sample(uv + params.uSpread * float2(-1.0, -1.0)) +
        uSource.Sample(uv + params.uSpread * float2(1.0, -1.0)) +
        uSource.Sample(uv + params.uSpread * float2(-1.0, 1.0)) +
        uSource.Sample(uv + params.uSpread * float2(1.0, 1.0)));
    return float4(toLinear(shown.rgb), shown.a);
}
