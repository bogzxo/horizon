// Temporal anti aliasing. The eye rays go through a different spot of their pixel every frame, so laying every frame
// over the last ones (moved along with the camera) is an edge sampled a dozen times, smooth, and the noise left in
// the soft shadows and the bounce light gets averaged out on the way. What the last frames say is kept to the
// colours of the neighbourhood of this pixel this frame (in YCoCg, by its mean and spread), so something that moved or
// came out from behind something doesn't leave a ghost. Brightness is weighed down before the blend so one blown out
// pixel doesn't flicker the ones round it.

#include <voxels/frame.slang>

BIND_TEXTURE(0) Sampler2D uScene;
BIND_TEXTURE(1) Sampler2D uPosition;
BIND_TEXTURE(2) Sampler2D uHistory;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uOut;

float3 toYCoCg(float3 c)
{
    return float3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b, 0.5 * c.r - 0.5 * c.b, -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}

float3 fromYCoCg(float3 c)
{
    return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z);
}

// Keeps a colour inside a box, pulling it towards the middle of the box along the line to it, rather than clamping
// each channel on its own, which shifts the hue
float3 clipToBox(float3 color, float3 low, float3 high)
{
    float3 middle = 0.5 * (high + low);
    float3 extent = 0.5 * (high - low) + 1e-5;
    float3 offset = color - middle;
    float3 ratio = abs(offset / extent);
    float largest = max(ratio.x, max(ratio.y, ratio.z));
    return largest > 1.0 ? middle + offset / largest : color;
}

float3 tame(float3 c) { return c / (1.0 + luminance(c)); }

// Last frame's picture at a point, through a Catmull-Rom filter, nine bilinear reads standing in for sixteen texels
// (Jimenez). Bilinear on its own is a little blur every frame, and laid over itself frame after frame that is a lot of
// blur, the picture went soft as soon as the camera moved and looked like it was traced at less than its size
float3 sampleHistory(float2 pixel, float2 size)
{
    float2 at = pixel - 0.5;
    float2 base = floor(at) + 0.5;
    float2 f = at - floor(at);

    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);

    float2 w12 = w1 + w2;
    float2 t0 = (base - 1.0) / size;
    float2 t3 = (base + 2.0) / size;
    float2 t12 = (base + w2 / w12) / size;

    float3 c = float3(0.0, 0.0, 0.0);
    c += uHistory.SampleLevel(float2(t0.x, t0.y), 0.0).rgb * (w0.x * w0.y);
    c += uHistory.SampleLevel(float2(t12.x, t0.y), 0.0).rgb * (w12.x * w0.y);
    c += uHistory.SampleLevel(float2(t3.x, t0.y), 0.0).rgb * (w3.x * w0.y);
    c += uHistory.SampleLevel(float2(t0.x, t12.y), 0.0).rgb * (w0.x * w12.y);
    c += uHistory.SampleLevel(float2(t12.x, t12.y), 0.0).rgb * (w12.x * w12.y);
    c += uHistory.SampleLevel(float2(t3.x, t12.y), 0.0).rgb * (w3.x * w12.y);
    c += uHistory.SampleLevel(float2(t0.x, t3.y), 0.0).rgb * (w0.x * w3.y);
    c += uHistory.SampleLevel(float2(t12.x, t3.y), 0.0).rgb * (w12.x * w3.y);
    c += uHistory.SampleLevel(float2(t3.x, t3.y), 0.0).rgb * (w3.x * w3.y);

    float total = (w0.x + w12.x + w3.x) * (w0.y + w12.y + w3.y);
    return max(c / total, 0.0);
}

float3 untame(float3 c) { return c / max(1.0 - luminance(c), 1e-4); }

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    FrameData f = frame();
    int2 size = int2(f.resolution.xy);
    int2 me = int2(id.xy);
    if (any(me >= size)) return;

    float3 current = tame(uScene.Load(int3(me, 0)).rgb);

    // The neighbourhood's mean and spread
    float3 m1 = float3(0.0, 0.0, 0.0), m2 = float3(0.0, 0.0, 0.0);
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float3 c = toYCoCg(tame(uScene.Load(int3(clamp(me + int2(x, y), int2(0, 0), size - 1), 0)).rgb));
            m1 += c;
            m2 += c * c;
        }
    }

    m1 /= 9.0;
    float3 spread = sqrt(max(m2 / 9.0 - m1 * m1, 0.0));
    float3 low = m1 - spread * 1.25, high = m1 + spread * 1.25;

    // Where this pixel was last frame. The sky is as far away as it gets, it only turns with the camera
    float4 position = uPosition.Load(int3(me, 0));
    float3 eye = f.cameraPos.xyz;
    float3 world;
    if (position.w < 0.0) world = eye + rayDirection(float2(me) + 0.5, f.resolution.xy) * 1e5;
    else world = eye + normalize(position.xyz - eye) * position.w;

    float2 was = project(world, f.prevViewProj, f.resolution.xy);
    float3 result = current;

    if (f.misc.w < 0.5 && all(was >= 0.0) && all(was < f.resolution.xy))
    {
        float3 history = tame(sampleHistory(was, f.resolution.xy));
        history = fromYCoCg(clipToBox(toYCoCg(history), low, high));
        result = lerp(history, current, f.history.y);
    }

    uOut[id.xy] = float4(untame(result), 1.0);
}
