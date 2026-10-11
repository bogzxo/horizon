// The eye rays, and nothing but. One ray a pixel, moved about inside the pixel a little every frame (the TAA puts them
// together into an edge with no stairs on it), through the world to the first thing it lands on, which is written
// down for the surface pass (surface.slang) packed into four numbers. On its own because a pass with every trace of
// the eye's in it (this, the water's two and the sun's shadow) needs so many registers the GPU can only keep a few
// of them going at once, and measured, the eye rays went from 2.5 ms to 1.6 on their own.

#include <voxels/tree.slang>

BIND_IMAGE(0) [[vk::image_format("rgba32f")]] RWTexture2D<float4> gPosition;
BIND_IMAGE(1) [[vk::image_format("rgba32ui")]] RWTexture2D<uint4> gHit;

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    FrameData f = frame();
    uint2 size = uint2(f.resolution.xy);
    if (any(id.xy >= size)) return;

    float2 pixel = float2(id.xy) + 0.5 + f.jitter.xy;
    float3 eye = f.cameraPos.xyz;
    float3 dir = rayDirection(pixel, f.resolution.xy);
    int steps = int(f.lod.w);
    bool underwater = f.misc.x > 0.5;

    // Looking up from under water the surface is gone through like the rest of it, the haze does the rest. Nothing is
    // seen much past a hundred blocks under there, and every voxel of water on the way is a step
    uint flags = underwater ? 0u : TRACE_STOP_AT_WATER;
    Hit h = trace(eye, dir, 0.0, underwater ? 128.0 : 1e6, 0.0, f.lod.x * f.lod.y, flags, steps);

    // How hard it worked, for the debug view, in where the colour would go
    if (int(f.history.z) == 1) h.color = float3(saturate(float(h.steps) / float(steps)), 0.0, 0.0);

    gPosition[id.xy] = h.hit ? float4(eye + dir * h.t, h.t) : float4(eye + dir * 1e5, -1.0);
    gHit[id.xy] = packHit(h);
}
