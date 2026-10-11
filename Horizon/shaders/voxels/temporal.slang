// The bounce light laid over what the frames before found at the same spot of the world. Every half size pixel finds
// where its surface was on last frame's picture (the camera's matrices from then), takes what was found there if it
// was the same surface (same way round, same distance from where the camera was), and puts this frame's ray in with
// it, one part in however many frames that spot has been seen for, up to history.x. A spot that wasn't on screen
// last frame starts from nothing and is noisy for a few frames, the atrous filter after this leans on its
// neighbours harder while it is (it knows by the count).
//
// It also keeps the mean of the brightness and of its square, which is how much the light at a spot wavers, for the
// filter to know what's noise and what's an edge.

#include <voxels/frame.slang>
#include <voxels/cache.slang>

BIND_TEXTURE(0) Sampler2D uRaw;
BIND_TEXTURE(1) Sampler2D uPosition;
BIND_TEXTURE(2) Sampler2D uNormal;
BIND_TEXTURE(3) Sampler2D uPrevLight;
BIND_TEXTURE(4) Sampler2D uPrevMoments;
BIND_TEXTURE(5) Sampler2D uPrevGeometry;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uLight;
BIND_IMAGE(1) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uMoments;
BIND_IMAGE(2) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uGeometry;

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    FrameData f = frame();
    int2 halfSize = int2(f.resolution.zw);
    if (any(int2(id.xy) >= halfSize)) return;

    uint frameNumber = uint(f.prevCameraPos.w);
    int2 full = min(int2(id.xy) * 2 + halfPick(frameNumber), int2(f.resolution.xy) - 1);
    float4 position = uPosition.Load(int3(full, 0));
    float4 raw = uRaw.Load(int3(int2(id.xy), 0));

    if (position.w < 0.0)
    {
        uLight[id.xy] = float4(0.0, 0.0, 0.0, 0.0);
        uMoments[id.xy] = float4(0.0, 0.0, 0.0, 0.0);
        uGeometry[id.xy] = float4(0.0, 0.0, 0.0, -1.0);
        return;
    }

    float3 n = uNormal.Load(int3(full, 0)).xyz;
    float3 p = position.xyz;
    float expected = length(p - f.prevCameraPos.xyz);

    // Where it was, between four texels of last frame's
    float2 was = project(p, f.prevViewProj, f.resolution.zw) - 0.5;
    int2 base = int2(floor(was));
    float2 fraction = was - float2(base);

    float4 history = float4(0.0, 0.0, 0.0, 0.0);
    float2 moments = float2(0.0, 0.0);
    float weights = 0.0;

    if (f.misc.w < 0.5)
    {
        for (int i = 0; i < 4; i++)
        {
            int2 offset = int2(i & 1, i >> 1);
            int2 q = base + offset;
            if (any(q < 0) || any(q >= halfSize)) continue;

            float4 geometry = uPrevGeometry.Load(int3(q, 0));
            bool same = geometry.w > 0.0 && dot(geometry.xyz, n) > 0.85 && abs(geometry.w - expected) < max(0.04 * expected, 0.35);
            if (!same) continue;

            float2 bilinear = lerp(1.0 - fraction, fraction, float2(offset));
            float w = bilinear.x * bilinear.y;
            history += uPrevLight.Load(int3(q, 0)) * w;
            moments += uPrevMoments.Load(int3(q, 0)).xy * w;
            weights += w;
        }
    }

    float frames = 0.0;
    if (weights > 0.02)
    {
        history /= weights;
        moments /= weights;
        frames = history.a;
    }

    frames = min(frames + 1.0, max(f.history.x, 1.0));
    float share = 1.0 / frames;

    float3 light = lerp(history.rgb, raw.rgb, share);
    float brightness = luminance(raw.rgb);
    float2 m = lerp(moments, float2(brightness, brightness * brightness), share);

    uLight[id.xy] = float4(light, frames);

    // And into the cache, once it has settled a bit, for the bounce rays that land here (cache.slang)
    if (frames >= 4.0) cacheWrite(p, n, light);
    uMoments[id.xy] = float4(m, 0.0, 0.0);
    uGeometry[id.xy] = float4(n, length(p - f.cameraPos.xyz));
}
