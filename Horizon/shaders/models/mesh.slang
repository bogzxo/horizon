// The mesh shader, what a Mesh3D is drawn with unless it brings its own. One sun and an ambient, a texture if
// there is one, a tint, and the colour of every vertex, which is as much lighting as a 2D engine that has just
// grown a third dimension needs to start with. It writes the G-buffer like everything else so a mesh can sit
// inside a lit renderer without the deferred pass falling over it, and says it is emissive, the sun has lit it
// already and the lamps of a flat world have no idea what to do with a sphere.
//
// The vertex layout is Vertex3D.cs, position, normal, texture coordinate, colour, in that order at those
// locations. A shader of your own that reads the same can be handed to Mesh3D.Shader.

#include <common/camera.slang>
#include <common/gbuffer.slang>

struct Params
{
    float4x4 uModel;      // local to world
    float4 uTint;
    float3 uSun;          // which way the sun shines, a unit vector
    float3 uSunColor;
    float3 uAmbient;
    float uEmissive;
    int uHasTexture;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_TEXTURE(0) Sampler2D uTexture;

struct Vertex
{
    float3 position : POSITION;
    float3 normal : NORMAL;
    float2 texCoords : TEXCOORD0;
    float4 color : COLOR0;
};

struct Varyings
{
    float4 position : SV_Position;
    float3 normal : NORMAL;
    float2 texCoords : TEXCOORD0;
    float4 color : COLOR0;
};

[shader("vertex")]
Varyings vsMain(Vertex input)
{
    Varyings output;
    float4 world = mul(float4(input.position, 1.0), params.uModel);
    output.position = toClip(world);

    // The normal goes through the rotation and scale of the model and none of its translation. A model scaled
    // unevenly wants the inverse transpose here, nobody draws one of those yet
    output.normal = normalize(mul(float4(input.normal, 0.0), params.uModel).xyz);
    output.texCoords = input.texCoords;
    output.color = input.color;
    return output;
}

[shader("fragment")]
GBuffer fsMain(Varyings input)
{
    float4 albedo = input.color * params.uTint;
    if (params.uHasTexture != 0) albedo *= uTexture.Sample(input.texCoords);
    if (albedo.a <= 0.0) discard;

    // Lambert, the amount of sun a surface catches is how squarely it faces it, and nothing from behind
    float3 normal = normalize(input.normal);
    float facing = max(dot(normal, -params.uSun), 0.0);
    float3 light = params.uAmbient + params.uSunColor * facing;

    // What glows shows as it is, the rest shows as much as the light lets it
    float3 lit = lerp(albedo.rgb * light, albedo.rgb, params.uEmissive);
    return writeFlat(float4(lit, albedo.a), 1.0);
}
