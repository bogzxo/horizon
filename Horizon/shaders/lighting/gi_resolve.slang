// The last step of the radiance cascades. Every pixel of the small picture takes the four probes of the nearest
// cascade around it, blends them by how near each is, and averages their rays, which is the light arriving there
// from every direction. That goes into the deferred pass as the light that isn't straight from a lamp.

struct Params
{
    float2 uSize;        // the small picture, in pixels
    float2 uProbeCount;  // probes of the nearest cascade
    float uProbeSpacing;
    float2 uProbeOffset; // where its grid starts against the picture, see PathTracedLighting2D.Offset
    float uDirections;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_TEXTURE(0) Sampler2D uTexCascade;
BIND_IMAGE(0) [[vk::image_format("rgba16f")]] RWTexture2D<float4> uResult;

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 texel = int2(id.xy);
    if (any(texel >= int2(params.uSize))) return;

    float2 at = (float2(texel) + 0.5 - params.uProbeOffset) / params.uProbeSpacing - 0.5;
    float2 low = floor(at);
    float2 weight = at - low;
    int2 count = int2(params.uProbeCount);
    int sqrtDirections = int(sqrt(params.uDirections) + 0.5);
    int directions = int(params.uDirections);

    float3 total = float3(0.0, 0.0, 0.0);
    for (int y = 0; y < 2; y++)
    {
        for (int x = 0; x < 2; x++)
        {
            int2 probe = clamp(int2(low) + int2(x, y), int2(0, 0), count - 1);
            float w = (x == 0 ? 1.0 - weight.x : weight.x) * (y == 0 ? 1.0 - weight.y : weight.y);

            float3 sum = float3(0.0, 0.0, 0.0);
            for (int d = 0; d < directions; d++)
            {
                int2 dirCell = int2(d % sqrtDirections, d / sqrtDirections);
                sum += uTexCascade.Load(int3(dirCell * count + probe, 0)).rgb;
            }

            total += sum / float(directions) * w;
        }
    }

    uResult[texel] = float4(total, 1.0);
}
