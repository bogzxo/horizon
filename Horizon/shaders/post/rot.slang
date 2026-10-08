// A picture rotting away, see RotTransition. Blotches of noise spread until they have eaten everything. A murky
// stain creeps ahead of each blotch and it has a sickly crust along its edge. It is drawn blended over the finished
// frame, so all this decides is the colour of the rot and how much of what is under it still shows.

#include <common/screen.slang>

struct Params
{
    // How much of the picture is gone, from 0 to 1
    float uCover;

    // Somewhere else in the noise every time, so no two transitions rot the same way
    float uSeed;

    // How many blotches fit across and up the screen
    float2 uCells;

    // How many blocks the screen is cut into, which keeps the rot as chunky as pixel art
    float2 uBlocks;

    // What is left where the rot has been, and the colour of the crust along the edge of it
    float3 uColor;
    float3 uEdgeColor;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// How wide the stain and the crust are, in the same units as the noise (0 to 1)
static const float STAIN = 0.16;
static const float CRUST = 0.05;

// Noise bunches up around the middle. Stretched by this much it is spread over all of 0 to 1,
// so the rot gets going the moment it starts instead of sitting there for a third of its time
static const float CONTRAST = 1.7;

// A direction for every corner of the grid, the same one every time it is asked for
float2 gradientAt(float2 corner)
{
    float angle = 6.2831853 * frac(sin(dot(corner, float2(127.1, 311.7)) + params.uSeed) * 43758.5453);
    return float2(cos(angle), sin(angle));
}

// Perlin noise, somewhere between -0.7 and 0.7
float perlin(float2 point)
{
    float2 corner = floor(point);
    float2 within = frac(point);
    float2 fade = within * within * within * (within * (within * 6.0 - 15.0) + 10.0);

    float bottomLeft = dot(gradientAt(corner), within);
    float bottomRight = dot(gradientAt(corner + float2(1.0, 0.0)), within - float2(1.0, 0.0));
    float topLeft = dot(gradientAt(corner + float2(0.0, 1.0)), within - float2(0.0, 1.0));
    float topRight = dot(gradientAt(corner + float2(1.0, 1.0)), within - float2(1.0, 1.0));

    return lerp(lerp(bottomLeft, bottomRight, fade.x), lerp(topLeft, topRight, fade.x), fade.y);
}

// A few layers of it on top of each other for ragged edges, squeezed into 0 to 1
float blotches(float2 point)
{
    float sum = 0.0;
    float weight = 0.5;
    float total = 0.0;

    for (int layer = 0; layer < 4; layer++)
    {
        sum += perlin(point) * weight;
        total += weight;
        point *= 2.03;
        weight *= 0.5;
    }

    return clamp(sum / total * CONTRAST + 0.5, 0.0, 1.0);
}

[shader("fragment")]
float4 fsMain(ScreenVaryings input) : SV_Target0
{
    // Every block of the screen rots as one
    float2 block = (floor(input.texCoords * params.uBlocks) + 0.5) / params.uBlocks;
    float noise = blotches(block * params.uCells);

    // The front moves through the noise as the cover goes up. It overshoots by the width of the stain and the crust,
    // so that at a cover of 1 even the last pixel to go is all the way gone
    float front = params.uCover * (1.0 + STAIN + CRUST);
    float behind = front - noise;

    // Not reached yet
    if (behind <= 0.0) return float4(0.0, 0.0, 0.0, 0.0);

    // First the stain, which is the rot showing through thinly. Then the crust, which hides what was there for good and darkens into the rot itself
    if (behind < STAIN)
    {
        float stain = behind / STAIN;
        return float4(lerp(params.uColor, params.uEdgeColor, 0.35), stain * stain * 0.75);
    }

    float crust = clamp((behind - STAIN) / CRUST, 0.0, 1.0);
    return float4(lerp(params.uEdgeColor, params.uColor, crust), 1.0);
}
