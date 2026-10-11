// What the eye rays landed on, made into a surface. Its colour (the tile of the atlas for the face, fading to the
// material's average when a texel is smaller than a pixel), what it glows by itself, and the sky where they landed
// on nothing. The sun's light and its shadow are the next pass's (sunlight.slang), the bounce light the one after.
//
// Water is done here. The ray stopped at its surface, a wave or two is worked out for which way the surface faces
// right there, one ray goes off the surface for the reflection and another carries on under it, bent the way water
// bends it, to whatever is at the bottom, losing red on the way (so shallows are turquoise and the deep is dark
// blue). How much of either is seen is Fresnel, the reflection at a low angle and the bottom looking down. From then
// on the pixel is the bottom, with what the water took off it taken off its colour, and the reflection on top.
//
// Out of here, per pixel. Where the surface is (and how far off it was, for the haze), which way it faces, its colour
// and the light on it that has nothing to do with the sun.

#include <voxels/tree.slang>
#include <voxels/sky.slang>

BIND_TEXTURE(0) Sampler2D<uint4> uHit;
BIND_IMAGE(0) [[vk::image_format("rgba32f")]] RWTexture2D<float4> gPosition;
BIND_IMAGE(1) [[vk::image_format("rgba16f")]] RWTexture2D<float4> gNormal;
BIND_IMAGE(2) [[vk::image_format("rgba16f")]] RWTexture2D<float4> gAlbedo;
BIND_IMAGE(3) [[vk::image_format("rgba16f")]] RWTexture2D<float4> gDirect;

// Which way the water faces at a point, a few waves going different ways at different speeds
float3 waterNormal(float2 xz, float time, float choppy)
{
    float2 g = float2(0.0, 0.0);
    const float4 waves[5] = {
        float4(0.83, 0.55, 0.62, 1.10),
        float4(-0.41, 0.91, 1.13, 1.55),
        float4(0.21, -0.98, 1.91, 2.10),
        float4(-0.72, -0.69, 2.73, 2.45),
        float4(0.97, -0.24, 4.10, 3.20)
    };

    for (int i = 0; i < 5; i++)
    {
        float2 dir = waves[i].xy;
        float frequency = waves[i].z;
        float amplitude = 0.06 / frequency;
        g += dir * frequency * amplitude * cos(dot(dir, xz) * frequency + time * waves[i].w);
    }

    return normalize(float3(-g.x * choppy, 1.0, -g.y * choppy));
}

// A quick look at something seen in the water, its colour lit by the sky and the sun without a shadow ray of its own
float3 shadeReflection(Hit r, float3 position, float footprint)
{
    FrameData f = frame();
    float3 albedo = surfaceColor(r, position, footprint);
    float ndl = saturate(dot(r.normal, f.sunDir.xyz));
    float3 ambient = skyColor(r.normal * 0.5 + float3(0.0, 0.5, 0.0)) * 0.6;
    return albedo * (f.sunColor.rgb * ndl * 0.7 * cloudShadow(position) + ambient) + surfaceEmission(r, albedo);
}

void writeOut(uint2 at, float4 position, float3 normal, float code, float4 albedo, float3 direct)
{
    gPosition[at] = position;
    gNormal[at] = float4(normal, code);
    gAlbedo[at] = albedo;
    gDirect[at] = float4(direct, 1.0);
}

[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    FrameData f = frame();
    uint2 size = uint2(f.resolution.xy);
    if (any(id.xy >= size)) return;

    float2 pixel = float2(id.xy) + 0.5 + f.jitter.xy;
    float3 eye = f.cameraPos.xyz;
    float3 dir = rayDirection(pixel, f.resolution.xy);
    float pixelAngle = f.lod.x;
    float slope = pixelAngle * f.lod.y;
    int steps = int(f.lod.w);
    bool underwater = f.misc.x > 0.5;

    float4 landed = gPosition[id.xy];
    Hit h = unpackHit(uHit.Load(int3(int2(id.xy), 0)), max(landed.w, 0.0), dir);

    // How hard the eye ray worked, blue for a few steps through green and yellow to red for the most there can be
    if (int(f.history.z) == 1)
    {
        float heat = h.color.r;
        float3 heatColor = saturate(float3(heat * 3.0 - 1.5, heat < 0.5 ? heat * 2.0 : 2.0 - heat * 2.0, 1.0 - heat * 3.0)) * 2.0;
        writeOut(id.xy, landed, -dir, 0.0, float4(0.0, 0.0, 0.0, 0.0), heatColor);
        return;
    }

    if (!h.hit)
    {
        // Off the edge of the world, looking down, is the sea, which goes on to the horizon. Only waves and the sky in them
        float seaLevel = f.water.x + 1.0;
        if (dir.y < -1e-4 && eye.y > seaLevel && !underwater)
        {
            float tSea = (seaLevel - eye.y) / dir.y;
            float3 at = eye + dir * tSea;
            if (at.x < 0.0 || at.z < 0.0 || at.x > f.world.x || at.z > f.world.z)
            {
                float3 surfaceNormal = waterNormal(at.xz, f.cameraPos.w, f.water.y * saturate(0.1 / (tSea * pixelAngle)));
                float cosine = saturate(dot(-dir, surfaceNormal));
                float fresnel = 0.02 + 0.98 * pow(1.0 - cosine, 5.0);
                float3 reflected = reflect(dir, surfaceNormal);
                reflected.y = abs(reflected.y);
                float3 deep = float3(0.012, 0.055, 0.085) * (0.2 + 0.8 * f.skyParams.w);
                float3 sea = skyForEye(at, reflected) * fresnel + deep * (1.0 - fresnel);
                writeOut(id.xy, float4(at, tSea), float3(0.0, 1.0, 0.0), 2.0, float4(0.0, 0.0, 0.0, 0.0), sea * h.transmit);
                return;
            }
        }

        float jitter = dice(id.xy, uint(f.prevCameraPos.w), 5u).x;
        writeOut(id.xy, landed, -dir, 0.0, float4(0.0, 0.0, 0.0, 0.0), skyForEye(eye, dir, jitter) * h.transmit);
        return;
    }

    float3 position = landed.xyz;
    float fogDistance = landed.w;
    float3 throughput = h.transmit;
    float3 extra = float3(0.0, 0.0, 0.0);

    if (h.kind == KIND_LIQUID && int(f.history.z) != 3)
    {
        // The surface, only the top of it gets waves
        float3 surfaceNormal = h.normal.y > 0.5 ? waterNormal(position.xz, f.cameraPos.w, f.water.y) : h.normal;
        float cosine = saturate(dot(-dir, surfaceNormal));
        float fresnel = 0.02 + 0.98 * pow(1.0 - cosine, 5.0);
        float footprint = slope * h.t;

        // What it reflects, at a coarser level of detail, nobody reads the reflection of a pebble. Past a few hundred blocks
        // a pixel of water is half a block across and the waves scatter what it reflects about anyway, it's the sky. Measured,
        // the reflections of the far sea skimming along it for hundreds of blocks were nearly half of the eye pass
        float3 reflected = reflect(dir, surfaceNormal);
        reflected.y = abs(reflected.y);
        float3 mirror;
        if (footprint < 0.6)
        {
            Hit r = trace(position + h.normal * 0.02, reflected, 0.0, 320.0, footprint, slope * 3.0 + 0.004, TRACE_COARSE, steps / 3);
            mirror = r.hit
                ? applyFog(shadeReflection(r, position + reflected * r.t, footprint + r.t * slope), position, reflected, r.t)
                : skyForEye(position, reflected);
        }
        else mirror = skyForEye(position, reflected);
        extra += mirror * fresnel;

        // And what is under it, bent and losing colour the deeper it goes. Nothing comes back through more than a few
        // dozen blocks of water, and at a low angle the reflection is nearly all of it, so the ray isn't sent at all
        float3 bent = refract(dir, surfaceNormal, 1.0 / 1.33);
        if (dot(bent, bent) < 0.5) bent = dir;
        // Further off the bottom fades into the deep over a band rather than stopping at a line, which showed as a ring
        // round the camera on the shallows
        float seeThrough = saturate((1.0 - footprint) / 0.5) * saturate((0.92 - fresnel) / 0.1);
        Hit under = noHit();
        if (seeThrough > 0.0) under = trace(position - h.normal * 0.02, bent, 0.0, 48.0, footprint, slope, 0u, steps / 2);

        float3 deep = float3(0.015, 0.07, 0.1) * (0.2 + 0.8 * f.skyParams.w);
        if (!under.hit || seeThrough <= 0.0)
        {
            extra += deep * (1.0 - fresnel);
            writeOut(id.xy, float4(position, fogDistance), surfaceNormal, 2.0, float4(0.0, 0.0, 0.0, 0.0), extra);
            return;
        }

        // The water's own murk, in for whatever light didn't make it through
        float3 kept = under.transmit * seeThrough;
        extra += deep * (1.0 - kept) * (1.0 - fresnel);
        throughput = kept * (1.0 - fresnel);

        position = position + bent * under.t;
        h = under;
        h.transmit = float3(1.0, 1.0, 1.0);
    }

    float footprint = pixelAngle * length(position - eye);
    float3 albedo = surfaceColor(h, position, footprint);
    float3 emission = surfaceEmission(h, albedo);
    float code = h.kind == KIND_FOLIAGE ? 3.0 : (h.kind == KIND_LEAVES ? 4.0 : 1.0);

    writeOut(id.xy, float4(position, fogDistance), h.normal, h.far ? -code : code,
             float4(albedo * throughput, h.far ? h.cell : 0.0), emission * throughput + extra);
}
