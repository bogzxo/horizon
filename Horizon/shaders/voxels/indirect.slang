// The light that bounces, the rays of it, traced at half size. One ray per half size pixel, from one of the four full
// size pixels under it (which one goes round every frame, so over four frames every pixel has been looked from), in a
// direction picked the way a matt surface takes light in (more of it straight out, see cosineDirection), so every ray
// counts the same and the average of enough of them is the light arriving there. One a frame is not enough, it's
// noise, which the temporal and atrous passes turn into light.
//
// This only goes and looks. Where the ray landed (or which way it went, if it landed on nothing) is written down for
// bounce_light.slang, which works out what's lit there. Two passes and not one for the same reason as the eye rays,
// a pass with two traces in it holds on to so many registers the GPU can only run a few of them at once.
//
// The rays go down the tree in a cone (lod.z), far off they stop at nodes as big as the cone is wide, they go through
// foliage and take leaves as solid (TRACE_COARSE), and they give up after 256 blocks. A bounce doesn't need to know
// about pebbles a hundred blocks away.

#include <voxels/tree.slang>

BIND_TEXTURE(0) Sampler2D uPosition;
BIND_TEXTURE(1) Sampler2D uNormal;
BIND_IMAGE(0) [[vk::image_format("rgba32f")]] RWTexture2D<float4> uBouncePosition;
BIND_IMAGE(1) [[vk::image_format("rgba32ui")]] RWTexture2D<uint4> uBounceHit;

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    FrameData f = frame();
    uint2 halfSize = uint2(f.resolution.zw);
    if (any(id.xy >= halfSize)) return;

    uint frameNumber = uint(f.prevCameraPos.w);
    int2 full = min(int2(id.xy) * 2 + halfPick(frameNumber), int2(f.resolution.xy) - 1);
    float4 position = uPosition.Load(int3(full, 0));
    if (position.w < 0.0)
    {
        uBouncePosition[id.xy] = float4(0.0, 0.0, 0.0, -2.0);
        uBounceHit[id.xy] = uint4(0, 0, 0, 0);
        return;
    }

    float4 normal = uNormal.Load(int3(full, 0));
    float3 n = normal.xyz;
    float3 p = position.xyz;
    float footprint = f.lod.x * length(p - f.cameraPos.xyz);

    float3 dir = cosineDirection(n, dice(id.xy, frameNumber, 1u).xy);

    // Off the surface by as much as the surface could be off, a far away node is a box bigger than what's in it
    float offset = normal.w < 0.0 ? footprint * 2.0 + 0.02 : max(0.004, footprint);
    float3 origin = p + n * offset;
    Hit h = trace(origin, dir, 0.0, 256.0, footprint, f.lod.z, TRACE_COARSE, int(f.lod.w) / 2);

    // Landed, where, or not, which way it went (so the next pass knows which bit of sky it saw)
    uBouncePosition[id.xy] = h.hit ? float4(origin + dir * h.t, h.t) : float4(dir, -1.0);
    uBounceHit[id.xy] = packHit(h);
}
