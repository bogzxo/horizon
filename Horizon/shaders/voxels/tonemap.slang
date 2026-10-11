// From light to a picture. The anti aliased frame (blown up to the size of the screen if it was traced smaller),
// the bloom on top, exposed, through the ACES curve (the filmic one everybody uses, Hill's fit of it, which rolls
// the highlights off instead of clipping them to white and keeps some colour in a sunset), a touch of vignette, a
// dither so the sky has no bands in it, and into the renderer's picture as sRGB bytes.

#include <voxels/frame.slang>

struct Params
{
    float2 uOutputSize;
    float uBloom;
    float uVignette;
    float uSaturation;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_BUFFER(7) StructuredBuffer<float> uExposure;

BIND_TEXTURE(0) Sampler2D uFrame;
BIND_TEXTURE(1) Sampler2D uBloom;
BIND_IMAGE(0) [[vk::image_format("rgba8")]] RWTexture2D<float4> uOutput;

float3 aces(float3 color)
{
    const float3x3 into = float3x3(
        0.59719, 0.07600, 0.02840,
        0.35458, 0.90834, 0.13383,
        0.04823, 0.01566, 0.83777);
    const float3x3 outOf = float3x3(
        1.60475, -0.10208, -0.00327,
        -0.53108, 1.10813, -0.07276,
        -0.07367, -0.00605, 1.07602);

    float3 v = mul(color, into);
    float3 a = v * (v + 0.0245786) - 0.000090537;
    float3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return saturate(mul(a / b, outOf));
}

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    if (any(float2(id.xy) >= params.uOutputSize)) return;

    FrameData f = frame();
    float2 uv = (float2(id.xy) + 0.5) / params.uOutputSize;

    float3 color = uFrame.SampleLevel(uv, 0.0).rgb;
    color += uBloom.SampleLevel(uv, 0.0).rgb * params.uBloom;
    color *= f.skyParams.z * max(uExposure[0], 0.01);

    float grey = luminance(color);
    color = max(lerp(float3(grey, grey, grey), color, params.uSaturation), 0.0);
    color = aces(color);

    float2 centered = uv - 0.5;
    color *= 1.0 - params.uVignette * dot(centered, centered) * 1.6;

    float3 encoded = linearToSrgb(color);
    float noise = (float(pcg(id.x * 1973u + id.y * 9277u + uint(f.prevCameraPos.w) * 26699u) & 255u) / 255.0 - 0.5) / 255.0;
    uOutput[id.xy] = float4(encoded + noise, 1.0);
}
