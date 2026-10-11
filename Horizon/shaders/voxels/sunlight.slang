// The sun on every surface the eye sees, through a shadow ray towards it. The sun isn't a point, it's half a degree
// across, so the ray goes off to a different bit of it every pixel and every frame, and the soft edge of a shadow
// settles out of that over a few frames in the TAA. At night it's the moon, the same light with another colour.
// Out comes the light falling on the surface, which the compose pass times by its colour. A pass of its own so the
// traces before it don't have to share their registers with this one.

#include <voxels/tree.slang>
#include <voxels/sky.slang>

BIND_TEXTURE(0) Sampler2D uPosition;
BIND_TEXTURE(1) Sampler2D uNormal;
BIND_TEXTURE(2) Sampler2D uAlbedo;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> gSun;

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    FrameData f = frame();
    uint2 size = uint2(f.resolution.xy);
    if (any(id.xy >= size)) return;

    float4 position = uPosition.Load(int3(int2(id.xy), 0));
    float4 normal = uNormal.Load(int3(int2(id.xy), 0));

    // Nothing there, or water with nothing under it, or the debug views, have no sun on them
    float code = abs(normal.w);
    if (position.w < 0.0 || code < 0.5 || code > 1.5 && code < 2.5 || int(f.history.z) == 1 || int(f.history.z) == 2)
    {
        gSun[id.xy] = float4(0.0, 0.0, 0.0, 0.0);
        return;
    }

    float3 L = f.sunDir.xyz;
    float3 n = normal.xyz;
    float ndl = saturate(dot(n, L));

    // Grass and leaves let some light through, a blade lit from behind still glows a bit
    if (code > 2.5) ndl = 0.35 + 0.65 * ndl;

    if (ndl <= 0.0 || max(f.sunColor.r, max(f.sunColor.g, f.sunColor.b)) <= 0.0)
    {
        gSun[id.xy] = float4(0.0, 0.0, 0.0, 0.0);
        return;
    }

    float3 p = position.xyz;
    float footprint = f.lod.x * length(p - f.cameraPos.xyz);
    float slope = f.lod.x * f.lod.y;
    int steps = int(f.lod.w);
    float3 d0 = dice(id.xy, uint(f.prevCameraPos.w), 0u);

    // Off the surface by as much as it could be off, a far away node is a box bigger than what's in it
    bool far = normal.w < 0.0;
    float cell = uAlbedo.Load(int3(int2(id.xy), 0)).a;
    float offset = far ? cell * 0.75 + 0.01 : max(0.004, footprint * 0.75);

    // Far off the shadow ray starts coarser and doesn't go as far, a mountain range away is plenty
    float3 towards = coneDirection(L, 0.0093, d0.xy);
    float reach = footprint > 1.0 ? 768.0 : 1536.0;
    float3 seen = shadow(p + n * offset, towards, footprint * (footprint > 1.0 ? 1.5 : 1.0), slope + 0.003, steps / 2, reach);

    gSun[id.xy] = float4(f.sunColor.rgb * ndl * seen * cloudShadow(p), 1.0);
}
