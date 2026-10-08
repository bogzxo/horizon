// The brick wall of the lighting test. One triangle over the whole view (see FullScreenPass, there are no vertices,
// the corners come out of the vertex number), which works out what of the wall every one of its pixels is looking
// at from where in the world it is. A brick wall made up on the spot, normal and specular maps included, there is
// nothing in the engine's own assets with either to test the lighting against. Writes all of the attachments of a
// DeferredRenderer2D, see its summary for what goes where.

#include <common/camera.slang>
#include <common/gbuffer.slang>

struct Params
{
    // Without them the wall is as flat as anything that came without a normal map, and without it none of it is shiny
    int uNormals;
    int uSpecular;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

struct Varyings
{
    float4 position : SV_Position;
    float2 worldPos : WORLDPOS;
};

[shader("vertex")]
Varyings vsMain(uint vertexId : SV_VertexID)
{
    float2 corner = float2((vertexId << 1) & 2, vertexId & 2) * 2.0 - 1.0;

    Varyings output;
    output.worldPos = worldOf(corner * 0.5 + 0.5);
    output.position = float4(corner, 0.0, 1.0);
    return output;
}

static const float2 BRICK_SIZE = float2(48.0, 24.0);
static const float MORTAR = 1.5; // how wide the gap between two bricks is, on either side of it
static const float BEVEL = 6.0;  // how far in from its edge a brick slopes down

[shader("fragment")]
GBuffer fsMain(Varyings input)
{
    float2 p = input.worldPos / BRICK_SIZE;

    // Every other row is shifted by half a brick
    p.x += fmod(abs(floor(p.y)), 2.0) * 0.5;

    float2 brick = floor(p);
    float2 within = frac(p);

    // No two bricks are quite the same colour
    float tint = frac(sin(dot(brick, float2(12.9898, 78.233))) * 43758.5453);
    float3 colour = lerp(float3(0.42, 0.3, 0.27), float3(0.58, 0.44, 0.37), tint);

    // How far it is to the nearest edge of the brick on each axis, in world units
    float2 edge = min(within, 1.0 - within) * BRICK_SIZE;
    bool mortar = min(edge.x, edge.y) < MORTAR;

    // One brick in four is glazed, the rest are as dull as the mortar
    float shine = 0.0;

    float2 normal = float2(0.0, 0.0);
    if (mortar)
    {
        colour = float3(0.2, 0.19, 0.2);
    }
    else
    {
        if (tint > 0.75)
        {
            colour = float3(0.2, 0.32, 0.42);
            shine = 0.9;
        }

        // Towards its edges a brick slopes away from its middle, which is what catches the light
        float2 away = sign(within - 0.5);
        float2 slope = clamp(1.0 - (edge - MORTAR) / BEVEL, 0.0, 1.0);
        normal = away * slope * 0.7;
    }

    GBuffer output;
    output.albedo = float4(colour, 1.0);
    output.surface = float4(params.uNormals != 0 ? normal * 0.5 + 0.5 : float2(0.5, 0.5), 0.0, 1.0);
    output.material = float4(params.uSpecular != 0 ? shine : 0.0, 0.0, 0.0, 1.0);
    return output;
}
