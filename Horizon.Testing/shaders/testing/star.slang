// The star in the shapes example, a Mesh2D's triangles spun round and breathing on the GPU. A Vertex2D is a
// position and a texture coordinate, and the star doesn't need a texture, so it uses the coordinate for its own
// stuff. x is how far out from the middle a vertex is (0 middle, 1 tip), y is 1 for the ring round the outside,
// which stays put while the star breathes. White hot in the middle, out to a colour that wanders round the
// rainbow at the tips (and the ring).

#include <common/camera.slang>

struct Params
{
    float2 uCentre;
    float uTime;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

struct VertexInput
{
    [[vk::location(0)]] float2 position : POSITION;
    [[vk::location(1)]] float2 texCoords : TEXCOORD0;
};

struct Varyings
{
    float4 position : SV_Position;
    float edge : EDGE;
};

[shader("vertex")]
Varyings vsMain(VertexInput input)
{
    float angle = params.uTime * 0.6;
    float breathe = 1.0 + 0.1 * sin(params.uTime * 2.2) * input.texCoords.x * (1.0 - input.texCoords.y);
    float2x2 spin = float2x2(cos(angle), sin(angle), -sin(angle), cos(angle));
    float2 turned = mul(input.position, spin) * breathe;

    Varyings output;
    output.edge = input.texCoords.x;
    output.position = toClip(float4(turned + params.uCentre, 0.0, 1.0));
    return output;
}

[shader("fragment")]
float4 fsMain(Varyings input) : SV_Target0
{
    float3 middle = float3(1.0, 0.97, 0.78);
    float3 tips = 0.55 + 0.45 * cos(params.uTime * 0.7 + float3(0.0, 2.1, 4.2));
    return float4(lerp(middle, tips, smoothstep(0.1, 0.9, input.edge)), 1.0);
}
