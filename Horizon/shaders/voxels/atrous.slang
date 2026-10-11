// The bounce light smoothed out over its neighbours, without smoothing over the edges of things. An a-trous wavelet
// filter, a 3 x 3 blur run a few times with the taps further apart every time (1, 2, 4 half size pixels), which is
// a wide blur for the price of a few small ones. A neighbour counts for less the more it faces another way, the
// further it is off the plane of this surface, and the more its brightness differs from this one's by more than
// the light here usually wavers (from the moments of the temporal pass). A spot seen for only a frame or two
// wavers a lot as far as anyone knows, so it is smoothed hard, and gets sharper as it settles.

#include <voxels/frame.slang>

struct Params
{
    int uStep;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uIn;
BIND_TEXTURE(1) Sampler2D uPosition;
BIND_TEXTURE(2) Sampler2D uNormal;
BIND_TEXTURE(3) Sampler2D uMoments;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uOut;

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    FrameData f = frame();
    int2 halfSize = int2(f.resolution.zw);
    int2 me = int2(id.xy);
    if (any(me >= halfSize)) return;

    float4 center = uIn.Load(int3(me, 0));
    int2 pick = halfPick(uint(f.prevCameraPos.w));
    int2 limit = int2(f.resolution.xy) - 1;
    float4 position = uPosition.Load(int3(min(me * 2 + pick, limit), 0));
    if (position.w < 0.0)
    {
        uOut[id.xy] = center;
        return;
    }

    float3 p = position.xyz;
    float3 n = uNormal.Load(int3(min(me * 2 + pick, limit), 0)).xyz;
    float2 moments = uMoments.Load(int3(me, 0)).xy;
    float frames = max(center.a, 1.0);

    // How much the light here wavers, more than it seems while it has hardly been seen
    float variance = max(moments.y - moments.x * moments.x, 0.0) + 0.6 / frames;
    float sigma = sqrt(variance) * 4.0 + 1e-3;
    float brightness = luminance(center.rgb);

    // How far off the plane a neighbour can be and still count, about a pixel's worth of world at this distance
    float pixel = f.lod.x * length(p - f.cameraPos.xyz) * 2.0;
    int step = params.uStep;
    float tolerance = pixel * float(step) * 2.0 + 0.05;

    const float kernel[3] = { 0.25, 0.5, 0.25 };
    float3 sum = float3(0.0, 0.0, 0.0);
    float weights = 0.0;

    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 q = me + int2(x, y) * step;
            if (any(q < 0) || any(q >= halfSize)) continue;

            float4 other = uIn.Load(int3(q, 0));
            int2 qFull = min(q * 2 + pick, limit);
            float4 qPosition = uPosition.Load(int3(qFull, 0));
            if (qPosition.w < 0.0) continue;

            float3 qNormal = uNormal.Load(int3(qFull, 0)).xyz;
            float wNormal = pow(saturate(dot(n, qNormal)), 32.0);
            float wPlane = exp(-abs(dot(n, qPosition.xyz - p)) / tolerance);
            float wLight = exp(-abs(luminance(other.rgb) - brightness) / sigma);
            float w = kernel[x + 1] * kernel[y + 1] * wNormal * wPlane * wLight;

            sum += other.rgb * w;
            weights += w;
        }
    }

    uOut[id.xy] = float4(weights > 1e-5 ? sum / weights : center.rgb, center.a);
}
