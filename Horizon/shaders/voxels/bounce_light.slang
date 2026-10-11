// What the bounce rays found, lit. Where one landed it takes what is lit there, the sun on it (with a shadow ray of its
// own), whatever it glows, and the light that bounced onto it before, which is read out of the radiance cache
// (cache.slang). That last bit is the trick that makes it every bounce and not one, a room lit by a window through a
// doorway gets the light of the room next door, which got it from the window a few frames ago. A face nobody has seen
// yet is a guess, a bit of sky. A ray that landed on nothing sees the sky.

#include <voxels/tree.slang>
#include <voxels/sky.slang>
#include <voxels/cache.slang>

BIND_TEXTURE(0) Sampler2D uBouncePosition;
BIND_TEXTURE(1) Sampler2D<uint4> uBounceHit;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uOut;

// The most any one ray brings back, so a ray that found the sun's reflection in a lake doesn't light the room for a second
static const float FIREFLY = 6.0;

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    FrameData f = frame();
    uint2 halfSize = uint2(f.resolution.zw);
    if (any(id.xy >= halfSize)) return;

    float4 landed = uBouncePosition.Load(int3(int2(id.xy), 0));

    // Nothing was looked from here (the eye saw the sky)
    if (landed.w < -1.5)
    {
        uOut[id.xy] = float4(0.0, 0.0, 0.0, 0.0);
        return;
    }

    uint4 packed = uBounceHit.Load(int3(int2(id.xy), 0));
    float3 light;

    if (landed.w < 0.0)
    {
        light = skyColor(landed.xyz) * (1.0 - 0.45 * f.sunColor.w) * unpackHit(packed, 0.0, landed.xyz).transmit;
    }
    else
    {
        float3 at = landed.xyz;
        float3 toward = normalize(at - f.cameraPos.xyz);
        Hit h = unpackHit(packed, landed.w, toward);
        float there = f.lod.x * length(at - f.cameraPos.xyz) + f.lod.z * landed.w;
        float3 albedo = h.color;
        float3 emission = surfaceEmission(h);

        float3 L = f.sunDir.xyz;
        float ndl = saturate(dot(h.normal, L));
        if (h.kind == KIND_FOLIAGE || h.kind == KIND_LEAVES) ndl = 0.35 + 0.65 * ndl;

        float3 sun = float3(0.0, 0.0, 0.0);
        if (ndl > 0.0)
        {
            float3 towards = coneDirection(L, 0.0093, dice(id.xy, uint(f.prevCameraPos.w), 2u).xy);
            float lift = h.far ? h.cell * 0.75 + 0.02 : max(0.01, there * 0.75);
            sun = f.sunColor.rgb * ndl * shadow(at + h.normal * lift, towards, there, f.lod.z, int(f.lod.w) / 4, 384.0, TRACE_COARSE) * cloudShadow(at);
        }

        // The bounces before this one, out of the cache, wherever the camera is
        float3 before;
        if (!cacheRead(at, h.normal, before)) before = skyColor(float3(0.0, 1.0, 0.0)) * 0.12;

        light = (emission + albedo * (sun + before)) * h.transmit;
    }

    float brightness = luminance(light);
    if (brightness > FIREFLY) light *= FIREFLY / brightness;

    uOut[id.xy] = float4(light, 1.0);
}
