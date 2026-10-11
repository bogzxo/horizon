// Everything put together for a pixel. What the surface glows (and the water's reflection) from the surface pass,
// the sun on it from the sunlight pass and the bounce light (half size, filtered), the last two times its colour, brought up to full size by taking the four half size pixels around this
// one and counting each by how near it is and whether it's the same surface (so the light of a wall doesn't bleed onto
// the floor in front of it). Then the haze for how far off it is, and the outline of the block the player points at.

#include <voxels/sky.slang>

BIND_TEXTURE(0) Sampler2D uPosition;
BIND_TEXTURE(1) Sampler2D uNormal;
BIND_TEXTURE(2) Sampler2D uAlbedo;
BIND_TEXTURE(3) Sampler2D uDirect;
BIND_TEXTURE(4) Sampler2D uLight;
BIND_TEXTURE(5) Sampler2D uSun;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uScene;

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    FrameData f = frame();
    int2 size = int2(f.resolution.xy);
    int2 me = int2(id.xy);
    if (any(me >= size)) return;

    float4 position = uPosition.Load(int3(me, 0));
    float3 direct = uDirect.Load(int3(me, 0)).rgb;
    float3 eye = f.cameraPos.xyz;
    bool underwater = f.misc.x > 0.5;

    if (position.w < 0.0)
    {
        float3 sky = direct;
        if (underwater) sky = applyUnderwater(sky, 80.0);
        uScene[id.xy] = float4(sky, 1.0);
        return;
    }

    float4 normal = uNormal.Load(int3(me, 0));
    float3 n = normal.xyz;
    float3 p = position.xyz;
    float4 albedo = uAlbedo.Load(int3(me, 0));

    // The bounce light, from the four half size pixels round this one, by how close and how alike
    int2 halfSize = int2(f.resolution.zw);
    int2 pick = halfPick(uint(f.prevCameraPos.w));
    float2 at = (float2(me) + 0.5) * 0.5 - 0.5;
    int2 base = int2(floor(at));
    float2 fraction = at - float2(base);
    float tolerance = f.lod.x * position.w * 4.0 + 0.05;

    float3 bounce = float3(0.0, 0.0, 0.0);
    float weights = 0.0;
    float3 nearest = float3(0.0, 0.0, 0.0);
    float nearestDistance = 1e9;

    for (int i = 0; i < 4; i++)
    {
        int2 offset = int2(i & 1, i >> 1);
        int2 q = clamp(base + offset, int2(0, 0), halfSize - 1);
        int2 qFull = min(q * 2 + pick, size - 1);
        float4 qPosition = uPosition.Load(int3(qFull, 0));
        if (qPosition.w < 0.0) continue;

        float3 qNormal = uNormal.Load(int3(qFull, 0)).xyz;
        float3 light = uLight.Load(int3(q, 0)).rgb;
        float plane = abs(dot(n, qPosition.xyz - p));

        float2 bilinear = lerp(1.0 - fraction, fraction, float2(offset));
        float w = bilinear.x * bilinear.y * pow(saturate(dot(n, qNormal)), 16.0) * exp(-plane / tolerance) + 1e-4;
        bounce += light * w;
        weights += w;

        float distance = plane + (1.0 - dot(n, qNormal));
        if (distance < nearestDistance)
        {
            nearestDistance = distance;
            nearest = light;
        }
    }

    bounce = weights > 1e-3 ? bounce / weights : nearest;

    float3 sun = uSun.Load(int3(me, 0)).rgb;
    float3 color = direct + albedo.rgb * (sun + bounce * f.misc.z);

    // The outline of the block being pointed at, drawn on the faces of it that are seen
    if (f.selected.w != 0 && normal.w >= 0.0)
    {
        int3 voxel = int3(floor(p - n * 0.01));
        if (all(voxel == f.selected.xyz))
        {
            float3 inside = p - float3(voxel);
            float2 face = abs(n.y) > 0.5 ? inside.xz : (abs(n.x) > 0.5 ? inside.zy : inside.xy);
            float edge = min(min(face.x, 1.0 - face.x), min(face.y, 1.0 - face.y));
            float width = 0.025 + f.lod.x * position.w * 1.2;
            if (edge < width) color = color * 0.15 + 0.02;
            else color *= 1.12;
        }
    }

    float3 dir = normalize(p - eye);
    color = underwater ? applyUnderwater(color, position.w) : applyFog(color, eye, dir, position.w);

    uScene[id.xy] = float4(color, 1.0);
}
