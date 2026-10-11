// What every pass of the voxel path tracer knows about the frame, one block of it in a storage buffer that the CPU
// writes once a frame (VoxelRenderer.FrameData, which this has to match, float4s all the way down so nobody argues
// about where a float3 starts). Plus the dice, every pass of a path tracer throws a lot of them.
//
// Units. Positions are in voxels, a voxel is one unit, the world goes from 0 to world.xyz. Matrices are row major and
// multiplied the System.Numerics way, mul(v, M), with no Vulkan flip in them, Y goes up the screen in clip space and
// down it in pixels.

struct FrameData
{
    float4x4 invViewProj;   // from clip space to the world, this frame, without the jitter
    float4x4 viewProj;      // and back
    float4x4 prevViewProj;  // last frame's, for finding where something was on screen
    float4 cameraPos;       // xyz the eye, w the time in seconds (for the waves and the wind)
    float4 prevCameraPos;   // xyz last frame's eye, w the number of the frame
    float4 sunDir;          // xyz towards the sun, w how far over the horizon it is (-1 to 1)
    float4 sunColor;        // rgb what the sun puts on a surface facing it, w how cloudy it is 0 to 1
    float4 moonDir;         // xyz towards the moon, w how bright the moon is
    float4 skyParams;       // x fog density, y how quickly the fog thins with height, z exposure, w how much of a day it is 0 to 1
    float4 world;           // xyz the size of the world in voxels, w how deep the tree is
    float4 resolution;      // xy the traced picture, zw the half size one the indirect light is traced at
    float4 jitter;          // xy where in its pixel this frame's ray goes, -0.5 to 0.5, zw the size of the output
    float4 lod;             // x how big a pixel is in radians, y the detail bias, z how wide the cone of a bounce ray is, w the most steps a ray takes
    float4 water;           // x the sea level, y how choppy, z how clear, w the tile of the atlas the water ripples come from (unused yet)
    int4 selected;          // xyz the voxel the player is pointing at, w whether they are pointing at one
    float4 misc;            // x 1 with the camera under water, y how much the foliage sways, z how strong the bounced light is, w 1 for the first frame (no history)
    float4 atlas;           // x tiles a row, y 1 / that, z textures on 1 or off 0, w texels a tile
    float4 history;         // x how many frames the bounce light is laid over at most, y TAA blend, z the debug view (VoxelRenderer.DebugView), w how long the last frame was
};

BIND_BUFFER(6) StructuredBuffer<FrameData> frameBlock;

FrameData frame() { return frameBlock[0]; }

/* Dice. PCG, which is quick, has no patterns anybody can see, and takes any number of seeds */

uint pcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

uint3 pcg3(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}

// Three numbers from 0 to 1 for a pixel on a frame, different every frame and from every neighbour
float3 dice(uint2 pixel, uint frameNumber, uint salt)
{
    return float3(pcg3(uint3(pixel.x, pixel.y, frameNumber * 7u + salt * 131u))) * (1.0 / 4294967296.0);
}

// A number from 0 to 1 for a voxel, the same every time, for whatever has to be different from block to block and not
// from frame to frame (which way a tuft of grass faces, how much darker this stone is than that one)
float hash3(int3 p)
{
    return float(pcg(uint(p.x) * 73856093u ^ uint(p.y) * 19349663u ^ uint(p.z) * 83492791u)) * (1.0 / 4294967296.0);
}

uint hash3u(int3 p)
{
    return pcg(uint(p.x) * 73856093u ^ pcg(uint(p.y) * 19349663u ^ uint(p.z) * 83492791u));
}

/* Colours */

float3 srgbToLinear(float3 c)
{
    return select(c <= 0.04045, c / 12.92, pow((c + 0.055) / 1.055, 2.4));
}

float3 linearToSrgb(float3 c)
{
    c = saturate(c);
    return select(c <= 0.0031308, c * 12.92, 1.055 * pow(c, 1.0 / 2.4) - 0.055);
}

float3 unpackColor(uint c)
{
    return srgbToLinear(float3(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF) / 255.0);
}

float luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

/* Directions */

// A direction round a normal, more of them straight out than to the side, which is how light falls on a matt
// surface, so every ray counts for the same and nothing has to be weighed
float3 cosineDirection(float3 n, float2 u)
{
    float r = sqrt(u.x);
    float phi = 6.28318530718 * u.y;
    float3 t = abs(n.y) < 0.999 ? normalize(cross(float3(0, 1, 0), n)) : float3(1, 0, 0);
    float3 b = cross(n, t);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - u.x)));
}

// A direction inside a cone round another, for soft shadows from a sun that isn't a point
float3 coneDirection(float3 axis, float angle, float2 u)
{
    float cosTheta = 1.0 - u.x * (1.0 - cos(angle));
    float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
    float phi = 6.28318530718 * u.y;
    float3 t = abs(axis.y) < 0.999 ? normalize(cross(float3(0, 1, 0), axis)) : float3(1, 0, 0);
    float3 b = cross(axis, t);
    return normalize(t * (sinTheta * cos(phi)) + b * (sinTheta * sin(phi)) + axis * cosTheta);
}

/* The screen */

// Where a point of the world lands on a picture of a size, in pixels from the top left, through a camera. Behind the
// camera is a long way off screen
float2 project(float3 world, float4x4 viewProj, float2 size)
{
    float4 clip = mul(float4(world, 1.0), viewProj);
    if (clip.w <= 1e-4) return float2(-1e6, -1e6);
    float2 ndc = clip.xy / clip.w;
    return float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5) * size;
}

// The ray through a point of the picture (pixels from the top left, fractions allowed)
float3 rayDirection(float2 pixel, float2 size)
{
    float2 ndc = float2(pixel.x / size.x * 2.0 - 1.0, 1.0 - pixel.y / size.y * 2.0);
    FrameData f = frame();
    float4 near = mul(float4(ndc, 0.0, 1.0), f.invViewProj);
    float4 far = mul(float4(ndc, 1.0, 1.0), f.invViewProj);
    return normalize(far.xyz / far.w - near.xyz / near.w);
}

// Which pixel of a 2 x 2 block the half size passes look at this frame, round the four in turn
int2 halfPick(uint frameNumber)
{
    const int2 order[4] = { int2(0, 0), int2(1, 1), int2(1, 0), int2(0, 1) };
    return order[frameNumber & 3u];
}

// A linear colour as sRGB bytes, red in the bottom, the way the palette packs them
uint linearToSrgbByte(float3 c)
{
    uint3 b = uint3(round(linearToSrgb(c) * 255.0));
    return b.x | b.y << 8 | b.z << 16;
}
