// The sky, the clouds and the haze. Not a physical atmosphere (that's a few lookup tables and an afternoon, see the
// plan), an analytic one that knows what a sky looks like at noon, at sunset and at night, which is most of the
// way there for a fraction of the cost. Everything is in the same units as the rest of the tracer, what a white
// surface would throw back, so a sky of 1 lights a white wall facing it at 1.

#include <voxels/frame.slang>

// The clouds are a layer of the world well above the tallest mountain, this thick
static const float CLOUD_BOTTOM = 330.0;
static const float CLOUD_TOP = 380.0;

/* Noise for the clouds, value noise on a hash, smooth enough, cheap enough */

float noise2(float2 p)
{
    float2 i = floor(p), f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = hash3(int3(int(i.x), 0, int(i.y)));
    float b = hash3(int3(int(i.x) + 1, 0, int(i.y)));
    float c = hash3(int3(int(i.x), 0, int(i.y) + 1));
    float d = hash3(int3(int(i.x) + 1, 0, int(i.y) + 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// How thick the clouds are over a point of the world, 0 for clear sky
float cloudCover(float2 xz)
{
    FrameData f = frame();
    float2 p = xz * 0.0045 + float2(f.cameraPos.w * 0.004, f.cameraPos.w * 0.0015);
    float n = 0.5 * noise2(p) + 0.25 * noise2(p * 2.03 + 7.1) + 0.125 * noise2(p * 4.01 + 3.7) + 0.0625 * noise2(p * 8.13 + 1.3);
    float coverage = lerp(0.62, 0.38, f.sunColor.w);
    return saturate((n - coverage) * 3.2);
}

// How much of the sun gets through the clouds to a point of the world
float cloudShadow(float3 position)
{
    FrameData f = frame();
    float3 sun = f.sunDir.xyz;
    if (sun.y <= 0.05) return 1.0;
    float t = ((CLOUD_BOTTOM + CLOUD_TOP) * 0.5 - position.y) / sun.y;
    float cover = cloudCover(position.xz + sun.xz * t);
    return 1.0 - 0.75 * cover;
}

/* The sky */

// The colour of the sky in a direction, without the sun's disc, the clouds or the stars. What the bounce rays see
float3 skyColor(float3 dir)
{
    FrameData f = frame();
    float day = f.skyParams.w;
    float3 sun = f.sunDir.xyz;
    float sunUp = f.sunDir.w;

    float up = saturate(dir.y);
    float3 zenithDay = float3(0.09, 0.22, 0.58);
    float3 horizonDay = float3(0.48, 0.62, 0.82);
    float3 zenithNight = float3(0.0025, 0.004, 0.012);
    float3 horizonNight = float3(0.006, 0.009, 0.02);

    float3 zenith = lerp(zenithNight, zenithDay, day);
    float3 horizon = lerp(horizonNight, horizonDay, day);

    // Sunrise and sunset, the horizon towards the sun goes orange, the sky over it pink
    float low = pow(1.0 - saturate(abs(sunUp) * 2.2), 2.0) * saturate(sunUp * 4.0 + 1.0);
    float2 flat = normalize(dir.xz + 1e-5);
    float towards = saturate(dot(flat, normalize(sun.xz + 1e-5)) * 0.5 + 0.5);
    horizon = lerp(horizon, float3(1.0, 0.42, 0.14) * 0.9, low * pow(towards, 3.0) * 0.85);
    zenith = lerp(zenith, float3(0.25, 0.14, 0.3) * 0.6, low * 0.4);

    float3 color = lerp(horizon, zenith, pow(up, 0.45));

    // Under the horizon it goes darker, nobody looks there for long
    if (dir.y < 0.0) color = horizon * lerp(1.0, 0.35, saturate(-dir.y * 3.0));

    // The glow round the sun
    float mu = dot(dir, sun);
    float halo = pow(saturate(mu), 12.0) * 0.35 + pow(saturate(mu), 180.0) * 1.2;
    color += f.sunColor.rgb * halo * saturate(sunUp * 6.0 + 0.4) * 0.6;

    return color;
}

// The night's extras, the moon and the stars
float3 nightSky(float3 dir)
{
    FrameData f = frame();
    float night = 1.0 - f.skyParams.w;
    if (night <= 0.01) return float3(0.0, 0.0, 0.0);

    float3 color = float3(0.0, 0.0, 0.0);
    float3 moon = f.moonDir.xyz;
    float mu = dot(dir, moon);
    color += float3(0.75, 0.8, 0.95) * smoothstep(0.99955, 0.9997, mu) * 1.6;
    color += float3(0.1, 0.12, 0.2) * pow(saturate(mu), 64.0) * 0.25;

    // Stars, a hash of where in the sky, fixed to it
    if (dir.y > 0.0)
    {
        float3 cell = floor(dir * 420.0);
        float h = hash3(int3(cell));
        float twinkle = 0.7 + 0.3 * sin(f.cameraPos.w * 3.0 + h * 50.0);
        color += step(0.9975, h) * float3(0.9, 0.92, 1.0) * twinkle * 0.8 * saturate(dir.y * 4.0);
    }

    return color * night;
}

// The clouds over a direction from the eye, their colour and how much of what is behind they hide. The steps start a
// different bit of the way in for every pixel and every frame (jitter, 0 to 1), or a few steps through a layer show
// up as stripes, every pixel stepping at the same depths. The TAA averages the jitter out
float4 clouds(float3 eye, float3 dir, float jitter = 0.5)
{
    if (dir.y <= 0.01) return float4(0.0, 0.0, 0.0, 0.0);

    FrameData f = frame();
    float tBottom = (CLOUD_BOTTOM - eye.y) / dir.y;
    float tTop = (CLOUD_TOP - eye.y) / dir.y;
    if (tTop <= 0.0) return float4(0.0, 0.0, 0.0, 0.0);
    float t0 = max(tBottom, 0.0);

    // A few steps through the layer, each with a look towards the sun for how lit it is
    const int STEPS = 6;
    float stepLength = (tTop - t0) / float(STEPS);
    float transmittance = 1.0;
    float3 light = float3(0.0, 0.0, 0.0);
    float3 sun = f.sunDir.xyz;
    float3 ambient = skyColor(float3(0.0, 1.0, 0.0)) * 0.9 + 0.02;
    float phase = 0.6 + 0.8 * pow(saturate(dot(dir, sun)), 6.0);

    for (int i = 0; i < STEPS; i++)
    {
        float3 p = eye + dir * (t0 + (float(i) + jitter) * stepLength);
        float height = saturate((p.y - CLOUD_BOTTOM) / (CLOUD_TOP - CLOUD_BOTTOM));
        float density = cloudCover(p.xz) * saturate(4.0 * height * (1.0 - height) * 1.4) * 0.06;
        if (density <= 0.0) continue;

        float toward = cloudCover(p.xz + sun.xz * 25.0);
        float lit = exp(-toward * 2.2) * saturate(sun.y * 3.0 + 0.2);
        float3 color = f.sunColor.rgb * lit * phase * 1.2 + ambient;

        float absorbed = 1.0 - exp(-density * stepLength);
        light += transmittance * absorbed * color;
        transmittance *= 1.0 - absorbed;
        if (transmittance < 0.02) break;
    }

    // Far off they melt into the haze
    float fade = saturate(1.0 - t0 / 9000.0);
    return float4(light * fade, (1.0 - transmittance) * fade);
}

// The whole sky in a direction, for the eye. Sun disc and all
float3 skyForEye(float3 eye, float3 dir, float jitter = 0.5)
{
    FrameData f = frame();
    float3 color = skyColor(dir) + nightSky(dir);

    float mu = dot(dir, f.sunDir.xyz);
    if (f.sunDir.w > -0.05) color += f.sunColor.rgb * smoothstep(0.99985, 0.99993, mu) * 60.0;

    float4 c = clouds(eye, dir, jitter);
    return color * (1.0 - c.a) + c.rgb;
}

/* The haze between the eye and whatever it sees */

// Thicker low down, thinning out with height, so mountains far off fade into the sky and the valleys fill with it
float3 applyFog(float3 color, float3 eye, float3 dir, float distance)
{
    FrameData f = frame();
    float density = f.skyParams.x;
    float falloff = f.skyParams.y;
    float base = f.water.x;

    // The integral of a density that falls off with height, along the ray
    float h0 = max(eye.y - base, 0.0);
    float dy = dir.y * distance;
    float amount = density * exp(-falloff * h0) * distance;
    if (abs(dy) > 0.01) amount *= (1.0 - exp(-falloff * dy)) / (falloff * dy);

    float extinction = 1.0 - exp(-amount);
    float3 flat = normalize(float3(dir.x, max(dir.y, 0.03), dir.z));
    float3 fogColor = skyColor(flat) * 0.95;
    fogColor += f.sunColor.rgb * pow(saturate(dot(dir, f.sunDir.xyz)), 8.0) * 0.25 * f.skyParams.w;
    return lerp(color, fogColor, extinction);
}

// Under water it's all green-blue and gone in a few dozen blocks
float3 applyUnderwater(float3 color, float distance)
{
    FrameData f = frame();
    float3 deep = float3(0.02, 0.09, 0.13) * (0.15 + 0.85 * f.skyParams.w);
    float3 kept = exp(-float3(0.12, 0.05, 0.04) * distance);
    return color * kept + deep * (1.0 - kept);
}
