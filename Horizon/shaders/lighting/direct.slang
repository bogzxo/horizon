// The lights of a DeferredRenderer2D and what blocks them, shared by everything that lights a point of the world
// directly, the deferred pass that puts the picture on screen and the path traced lighting. A pixel only looks at
// the lights of its tile of the screen (see light_tiles.slang), and a shadow is a few steps through the signed
// distance field of the occlusion map (see sdf_build.slang) rather than a walk from cell to cell.
// DeferredRenderer2D.BindLighting sets all of this, the shader that includes it puts LIGHTING_PARAMS in its Params.

#include <common/camera.slang>

// Must match DeferredRenderer2D.LightData (48 bytes).
struct Light
{
    float2 position;
    float radius;
    float intensity;
    float3 color;
    float height;
    float glow;
    float castsShadows;
    float size;
    float padding;
};

// How many lights a tile of the screen can hold, counting the count itself. Must match LightTiles.cs.
static const uint TILE_SIZE = 32u;
static const uint TILE_WORDS = 16u;
static const uint TILE_LIGHTS = TILE_WORDS - 1u;

#define LIGHTING_PARAMS \
    int uLightCount; \
    int uShadows; \
    int uHasField; \
    float uShininess; \
    float uSpecularIntensity; \
    float uShadowSoftness; \
    float2 uFieldOrigin; \
    float2 uFieldCellSize; \
    float2 uFieldSize; \
    float2 uTileCounts; \
    float2 uTileScreen; \
    int uHasSprites; \
    float uSpritePixelWorld;

// Everything that blocks light as the marches see it, see LIGHTING_FIELD.
struct Field
{
    bool hasWalls;           // whether there is an occlusion map
    float2 origin;           // where its field starts in the world
    float2 cellSize;         // the size of a texel of it, in world units
    float2 size;             // how many texels it is
    bool hasSprites;         // whether the sprites that block light left a field of their own
    float spritePixelWorld;  // the size of a texel of that, in world units
};

Field makeField(int hasWalls, float2 origin, float2 cellSize, float2 size, int hasSprites, float spritePixelWorld)
{
    Field field;
    field.hasWalls = hasWalls != 0;
    field.origin = origin;
    field.cellSize = cellSize;
    field.size = size;
    field.hasSprites = hasSprites != 0;
    field.spritePixelWorld = spritePixelWorld;
    return field;
}

// The field out of a Params that has LIGHTING_PARAMS in it.
#define LIGHTING_FIELD(p) makeField(p.uHasField, p.uFieldOrigin, p.uFieldCellSize, p.uFieldSize, p.uHasSprites, p.uSpritePixelWorld)

// Whether there is anything to march at all.
bool hasAnything(Field field)
{
    return field.hasWalls || field.hasSprites;
}

// How big a texel of the finest field is, in world units, which is how closely the marches look at things.
float fieldTexel(Field field)
{
    float texel = 1e9;
    if (field.hasWalls) texel = min(texel, max(field.cellSize.x, field.cellSize.y));
    if (field.hasSprites) texel = min(texel, field.spritePixelWorld);
    return texel < 1e9 ? texel : 1.0;
}

BIND_BUFFER(2) StructuredBuffer<Light> lights;

// For every tile, how many lights reach into it and then which, see light_tiles.slang.
BIND_BUFFER(3) StructuredBuffer<uint> lightTiles;

// The signed distance field of the occlusion map, in world units, negative inside of a wall. Unit 3.
BIND_TEXTURE(3) Sampler2D uTexField;

// The signed distance field of the sprites that block light, over the picture, in world units, negative inside of
// one, and in the green the same for everything that blocks or glows. Unit 5, see SpriteShadows.
BIND_TEXTURE(5) Sampler2D uTexSprites;

// How far the nearest thing that blocks light is from a point of the world, negative inside one, counting what
// glows as blocking if asked (the path traced lighting does, it is what lights things). Everything past the edge
// of the occlusion map is open as far as the map goes, and the sprites only block on the picture.
float wallDistance(float2 position, Field field, bool glowing)
{
    float nearest = 1e9;

    if (field.hasWalls)
    {
        float2 texel = (position - field.origin) / (field.cellSize * field.size);
        float2 inside = clamp(texel, 0.0, 1.0);
        float distance = uTexField.SampleLevel(inside, 0.0).r;

        // Outside of the field the nearest wall is at least as far as the edge of it
        float2 outside = max(abs(texel - 0.5) - 0.5, 0.0) * field.cellSize * field.size;
        nearest = any(outside > 0.0) ? max(distance, 0.0) + length(outside) : distance;
    }

    if (field.hasSprites)
    {
        float2 picture = pictureOf(position);
        if (all(picture >= 0.0) && all(picture <= 1.0))
        {
            float2 sprites = uTexSprites.SampleLevel(picture, 0.0).rg;
            nearest = min(nearest, glowing ? sprites.g : sprites.r);
        }
    }

    return nearest;
}

float wallDistance(float2 position, Field field)
{
    return wallDistance(position, field, false);
}

// Walks a ray that starts inside a wall out of it, which is smooth in where it starts (stepping out a texel at a
// time draws contours over everything solid, every pixel of a floor is inside the floor). Something solid is lit
// as what it is, the face of it that is drawn, not the inside of a wall, so its own wall never shadows it whichever
// way the ray goes. The field only says how deep the point is, the way out along the ray is that over how squarely
// the ray meets the surface, which the slope of the field gives, with a floor on it for rays going the other way.
// False for a ray that never gets out, which is lit as if the wall weren't there. Leaves t at the surface.
bool leaveWall(float2 from, float2 direction, inout float t, Field field, bool glowing)
{
    float d = wallDistance(from, field, glowing);
    if (d > 0.0) return true;

    float texel = fieldTexel(field);
    float2 slope = float2(
        wallDistance(from + float2(texel, 0.0), field, glowing) - wallDistance(from - float2(texel, 0.0), field, glowing),
        wallDistance(from + float2(0.0, texel), field, glowing) - wallDistance(from - float2(0.0, texel), field, glowing));
    float facing = dot(slope, slope) > 1e-6 ? max(dot(normalize(slope), direction), 0.25) : 1.0;
    float least = 0.5 * texel;

    for (int i = 0; i < 16; i++)
    {
        t += max(-d / facing, least);
        d = wallDistance(from + direction * t, field, glowing);
        if (d > 0.0) return true;
    }

    return false;
}

bool leaveWall(float2 from, float2 direction, inout float t, Field field)
{
    return leaveWall(from, direction, t, field, false);
}

// The index of the first word of the tile a point of the picture (0 to 1) is in, or -1 off the picture.
int tileOf(float2 picture, float2 tileCounts, float2 tileScreen)
{
    if (any(picture < 0.0) || any(picture > 1.0)) return -1;

    int2 tile = clamp(int2(picture * tileScreen / float(TILE_SIZE)), int2(0, 0), int2(tileCounts) - 1);
    return (tile.y * int(tileCounts.x) + tile.x) * int(TILE_WORDS);
}

// How much of a light gets to a point, going through the distance field towards it. A light with a size isn't
// hidden all at once, the way to it narrows to a penumbra along the edge of whatever is in the way. How wide the
// penumbra is follows from how near the way passes to a wall and how far along it that is, and the two readings of
// the field either side of the nearest point say where that was better than either of them alone.
float shadow(float2 from, float2 to, float size, Field field)
{
    float2 way = to - from;
    float distance = length(way);
    if (distance < 0.001) return 1.0;
    float2 direction = way / distance;

    // A point inside something solid is lit as the face of it the light sees, the way out is walked first and the
    // march starts clear of the surface
    float t = 0.0;
    if (!leaveWall(from, direction, t, field)) return 1.0;
    float surface = t;
    if (t > 0.0) t += 0.5 * fieldTexel(field);

    float visible = 1.0;
    float previous = 1e9;
    for (int step = 0; step < 32; step++)
    {
        if (t >= distance) break;

        float d = wallDistance(from + direction * t, field);
        if (d < 0.05) return 0.0;

        // The nearer the way passes to a wall, the less of a sized light is seen past it. Closing in on a wall, the
        // last two readings say where the nearest point was, going away from one they say nothing of the sort
        if (size > 0.0)
        {
            float y = d < previous ? d * d / (2.0 * previous) : 0.0;
            float nearest = sqrt(max(d * d - y * y, 0.0));
            visible = min(visible, nearest * distance / (size * max(t - surface - y, 0.5)));
            previous = d;
        }

        t += max(d, 0.5);
    }

    visible = clamp(visible, 0.0, 1.0);
    return visible * visible * (3.0 - 2.0 * visible);
}

// The light every light of a tile throws straight onto a point of the world. What is shiny mirrors the lights
// (highlights, in the light's own colour) and what hangs in the air around a light (glow) come out separately, they
// aren't multiplied by the surface.
float3 directLight(
    float2 position, float2 picture, float3 normal, bool hasNormal, float shine,
    int lightCount, bool shadows, float shininess, float softness,
    Field field, float2 tileCounts, float2 tileScreen,
    out float3 highlights, out float3 glow)
{
    float3 light = float3(0.0, 0.0, 0.0);
    highlights = float3(0.0, 0.0, 0.0);
    glow = float3(0.0, 0.0, 0.0);

    int tile = tileOf(picture, tileCounts, tileScreen);
    if (tile < 0) return light;

    uint count = min(lightTiles[tile], TILE_LIGHTS);
    for (uint n = 0; n < count; n++)
    {
        Light l = lights[lightTiles[tile + 1 + int(n)]];

        float2 toLight = l.position - position;
        float distanceSquared = dot(toLight, toLight);
        float radiusSquared = l.radius * l.radius;
        if (distanceSquared >= radiusSquared) continue;

        // Brightest in the middle, fading smoothly to nothing at the edge
        float falloff = 1.0 - distanceSquared / radiusSquared;
        falloff *= falloff;

        float reach = l.intensity * falloff;
        if (shadows && hasAnything(field) && l.castsShadows > 0.5)
        {
            reach *= shadow(position, l.position, l.size + softness, field);
            if (reach <= 0.0) continue;
        }

        float3 direction = normalize(float3(toLight, l.height));

        // A surface turned towards the light catches more of it than a flat one, one turned away less
        float facing = hasNormal ? clamp(dot(normal, direction) / max(direction.z, 0.05), 0.0, 2.0) : 1.0;

        light += l.color * (reach * facing);
        glow += l.color * (reach * falloff * l.glow);

        if (shine > 0.0)
        {
            // What is shiny mirrors the light itself. We are looking straight at the world, so it shows wherever the
            // surface is turned halfway between us and the light
            float3 halfway = normalize(direction + float3(0.0, 0.0, 1.0));
            highlights += l.color * (reach * shine * pow(max(dot(normal, halfway), 0.0), shininess));
        }
    }

    return light;
}

// The normal a surface of the G-buffer has, flat towards the viewer unless there is a normal map saying otherwise.
float3 surfaceNormal(float4 surface, out bool hasNormal)
{
    float3 normal = float3(surface.rg * 2.0 - 1.0, 0.0);
    hasNormal = dot(normal.xy, normal.xy) > 0.0025;
    normal.z = sqrt(max(1.0 - dot(normal.xy, normal.xy), 0.0));
    return normal;
}
