// Sorts the lights of a frame into the tiles of the screen, so a pixel only looks at the lights that reach it. Every
// tile is TILE_SIZE pixels square and holds a count and up to TILE_LIGHTS light indices. One thread a tile, which
// for a screen of a few hundred tiles and a few dozen lights is nothing at all.

#include <lighting/direct.slang>

struct Params
{
    LIGHTING_PARAMS
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_BUFFER(4) RWStructuredBuffer<uint> tilesOut;

[shader("compute")]
[numthreads(8, 8, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    int2 counts = int2(params.uTileCounts);
    if (id.x >= uint(counts.x) || id.y >= uint(counts.y)) return;

    // The box of the world the tile looks at, from its corners on the picture
    float2 lowPicture = float2(id.xy) * float(TILE_SIZE) / params.uTileScreen;
    float2 highPicture = float2(id.xy + 1) * float(TILE_SIZE) / params.uTileScreen;
    float2 a = worldOf(lowPicture), b = worldOf(highPicture);
    float2 low = min(a, b), high = max(a, b);

    int first = (int(id.y) * counts.x + int(id.x)) * int(TILE_WORDS);
    uint found = 0;

    for (int i = 0; i < params.uLightCount && found < TILE_LIGHTS; i++)
    {
        Light l = lights[i];
        float2 nearest = clamp(l.position, low, high);
        float2 away = l.position - nearest;
        if (dot(away, away) >= l.radius * l.radius) continue;

        tilesOut[first + 1 + int(found)] = uint(i);
        found++;
    }

    tilesOut[first] = found;
}
