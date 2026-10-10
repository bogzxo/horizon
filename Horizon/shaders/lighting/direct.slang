// The lights of a DeferredRenderer2D and what gets in their way. Everything that lights a point of the world
// straight from a lamp includes this, the deferred pass that puts the picture on screen and the path tracer when
// it wants to know how bright a wall is. A pixel only bothers with the lights of its own tile of the screen (see
// light_tiles.slang), and a shadow is a handful of hops through a signed distance field (see sdf_rows.slang and
// sprite_field.slang) rather than a crawl from cell to cell like some kind of animal.
// DeferredRenderer2D.BindLighting sets the lot up, the shader that includes this puts LIGHTING_PARAMS in its Params.

#include <common/camera.slang>

// Must match DeferredRenderer2D.LightData (80 bytes), so no, you can't just add a float.
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
    float spriteShadow;   // how much the sprites that block light darken this one, 0 for not at all, 1 for all the way
    float2 direction;     // which way a spot shines, or a directional light's rays go, a unit vector
    float cosInner;       // a spot is full inside of this angle (its cosine)
    float cosOuter;       // and gone past this one, below -1 for a light that shines every way
    float type;           // 0 point, 1 spot, 2 directional, see LightType
    float reach;          // how far a directional light's shadows go looking for something in the way
    float2 padding;
};

static const float LIGHT_POINT = 0.0;
static const float LIGHT_SPOT = 1.0;
static const float LIGHT_DIRECTIONAL = 2.0;

// How many lights a tile of the screen can hold, counting the count itself. Must match LightTiles.cs.
static const uint TILE_SIZE = 32u;
static const uint TILE_WORDS = 16u;
static const uint TILE_LIGHTS = TILE_WORDS - 1u;

#define LIGHTING_PARAMS \
    int uLightCount; \
    int uShadows; \
    int uHasField; \
    int uShadowsInside; \
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

// Everything that blocks light, the way the marches see it. Made once a thread by LIGHTING_FIELD and then handed
// about, because it carries a few numbers that are worked out once so the marches don't have to work them out at
// every single step. The world to picture mapping in particular. That used to be a full 4x4 matrix multiply per
// step per ray, which for a few hundred thousand rays of forty steps is a galactus amount of multiplying to find
// out where on the screen you are, when a 2D camera is an offset and a scale and nothing else.
struct Field
{
    bool hasWalls;           // whether there is an occlusion map
    float2 origin;           // where its field starts in the world
    float2 cellSize;         // the size of a texel of it, in world units
    float2 extent;           // how big the whole field is, in world units
    float2 invExtent;        // and one over that, for the texel of a world position without a divide
    bool hasSprites;         // whether the sprites that block light left a field of their own
    float spritePixelWorld;  // the size of a texel of that, in world units
    bool insideShadows;      // whether a point inside a wall takes the shadows its face does, see DeferredRenderer2D.ShadowsInsideWalls
    float2 pictureScale;     // world to picture, picture = world * scale + offset. Orthographic, or this is a lie
    float2 pictureOffset;
};

Field makeField(int hasWalls, float2 origin, float2 cellSize, float2 size, int hasSprites, float spritePixelWorld, int insideShadows)
{
    Field field;
    field.hasWalls = hasWalls != 0;
    field.origin = origin;
    field.cellSize = cellSize;
    field.extent = cellSize * size;
    field.invExtent = 1.0 / max(field.extent, 1e-6);
    field.hasSprites = hasSprites != 0;
    field.spritePixelWorld = spritePixelWorld;
    field.insideShadows = insideShadows != 0;

    // Three points through the real matrix and the mapping falls out, that is the whole trick. The deferred
    // renderer only ever has a Camera2D looking at it, which is orthographic, so the mapping is affine and w is 1
    field.pictureOffset = pictureOf(float2(0.0, 0.0));
    field.pictureScale = float2(pictureOf(float2(1.0, 0.0)).x, pictureOf(float2(0.0, 1.0)).y) - field.pictureOffset;
    return field;
}

// The field out of a Params that has LIGHTING_PARAMS in it.
#define LIGHTING_FIELD(p) makeField(p.uHasField, p.uFieldOrigin, p.uFieldCellSize, p.uFieldSize, p.uHasSprites, p.uSpritePixelWorld, p.uShadowsInside)

// Where on the picture (0 to 1) a point of the world lands, the cheap way. See Field.
float2 pictureOfFast(float2 world, Field field)
{
    return world * field.pictureScale + field.pictureOffset;
}

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

// How far the nearest wall of the occlusion map is from a point of the world, negative inside one. Everything past
// the edge of the map is open as far as the map goes.
float wallsDistance(float2 position, Field field)
{
    if (!field.hasWalls) return 1e9;

    float2 texel = (position - field.origin) * field.invExtent;
    float2 inside = clamp(texel, 0.0, 1.0);
    float distance = uTexField.SampleLevel(inside, 0.0).r;

    // Inside the field, which is nearly always, that is the answer and we are out of here before the square
    // root below gets a look in. Outside of it the nearest wall is at least as far as the edge of the field
    float2 outside = max(abs(texel - 0.5) - 0.5, 0.0);
    if (all(outside <= 0.0)) return distance;
    return max(distance, 0.0) + length(outside * field.extent);
}

// How far the nearest sprite that blocks light is, negative inside one, counting what glows as blocking if asked
// (the path traced lighting does, a lamp is a thing a ray stops at). The sprites only exist on the picture.
float spritesDistance(float2 position, Field field, bool glowing)
{
    if (!field.hasSprites) return 1e9;

    float2 picture = pictureOfFast(position, field);
    if (any(picture < 0.0) || any(picture > 1.0)) return 1e9;

    float2 sprites = uTexSprites.SampleLevel(picture, 0.0).rg;
    return glowing ? sprites.g : sprites.r;
}

// How far the nearest thing that blocks light is from a point of the world, walls and sprites alike.
float wallDistance(float2 position, Field field, bool glowing)
{
    return min(wallsDistance(position, field), spritesDistance(position, field, glowing));
}

float wallDistance(float2 position, Field field)
{
    return wallDistance(position, field, false);
}

// Walks a ray that starts inside a wall out of it. Something solid is lit as what it is, the face of it that is
// drawn, not the inside of a wall, so its own wall never shadows it whichever way the ray goes. It has to be smooth
// in where it starts. Stepping out a texel at a time drew contour lines over every floor in the game (every pixel
// of a floor is inside the floor, think about it), so instead the field says how deep the point is and the slope
// of the field says how squarely the ray meets the surface, and the way out is the one over the other, with a floor
// on the squareness for rays that are basically going along the wall. False for a ray that never gets out, which is
// lit as if the wall weren't there. Leaves t at the surface.
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

// How much of a light gets to a point, by marching the distance field towards it. A light with a size isn't hidden
// all at once, the way to it narrows to a penumbra along the edge of whatever is in the way. How wide the penumbra
// is follows from how near the way passes to a wall and how far along it that was, and the two readings of the
// field either side of the nearest point say where that was better than either of them alone.
// A sprite in the way only takes spriteShadow of the light (1 for all of it, which is what a wall takes), so a
// fighter can throw a shadow without putting the lantern out. And a sprite the light is inside of, a fighter
// standing right in front of the lamp, doesn't shadow it at all. The march walks on through every sprite it meets,
// and one it is still inside of when it reaches the light is one the light is behind, which is the fighter in front
// of the lamp, so that one is let off. A few steps of a texture, no colliders, no lists, no fucking about.
// A point inside a wall (the body of the ground under a fighter) takes no sprite shadows at all, a sprite's shadow
// stops at the tiles rather than running on down through them.
float shadow(float2 from, float2 to, float size, Field field, float spriteShadow)
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
    float texel = fieldTexel(field);
    if (t > 0.0) t += 0.5 * texel;

    // The body of something solid (the ground under a fighter) is lit as its face and, unless asked, takes no
    // shadows at all, the floor stops every shadow at its edge rather than showing it running on down through it.
    // Asked, it takes the shadows of the walls its face does and still none of the sprites
    bool inWall = wallsDistance(from, field) < 0.0;
    if (inWall && !field.insideShadows) return 1.0;

    float visible = 1.0;
    float through = 1.0;
    float previous = 1e9;
    bool sprites = field.hasSprites && !inWall;
    bool inside = false;
    for (int step = 0; step < 32; step++)
    {
        if (t >= distance) break;

        float2 at = from + direction * t;
        float d = sprites ? wallsDistance(at, field) : wallDistance(at, field, false);

        if (sprites)
        {
            // Sprites on their own, which the march goes through, a step at a time while inside of one
            float ds = spritesDistance(at, field, false);
            if (ds < 0.05)
            {
                inside = true;
                t += max(abs(ds), 0.5 * texel);
                continue;
            }

            // Out the other side of one, which was in the way after all
            if (inside)
            {
                inside = false;
                through *= 1.0 - spriteShadow;
                if (through <= 0.0) return 0.0;
            }

            d = min(d, ds);
        }

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

    // Still inside of a sprite at the light, the light is behind that one and it throws no shadow from it
    visible = clamp(visible, 0.0, 1.0);
    return visible * visible * (3.0 - 2.0 * visible) * through;
}

float shadow(float2 from, float2 to, float size, Field field)
{
    return shadow(from, to, size, field, 1.0);
}

// How much of a light arrives at a point of the world, before the surface has its say. Its brightness over the
// distance (a point and a spot fade to nothing at their radius, a directional light is the same everywhere), the
// cone of a spot, and the shadow of whatever is in the way. Where the light is seen from, for the normal maps and
// the highlights, comes out in toLight, which for a directional light is far off along its rays.
// 0 for a light that doesn't get here at all.
float lightReach(Light l, float2 position, bool shadows, float softness, Field field, out float2 toLight, out float falloff)
{
    falloff = 1.0;
    if (l.type == LIGHT_DIRECTIONAL)
    {
        // The rays all come the one way, from as far off as the light says it looks for shadows
        toLight = -l.direction * l.reach;
    }
    else
    {
        toLight = l.position - position;
        float distanceSquared = dot(toLight, toLight);
        float radiusSquared = l.radius * l.radius;
        if (distanceSquared >= radiusSquared) return 0.0;

        // Brightest in the middle, fading smoothly to nothing at the edge. Squared distances all the way, nobody
        // needs the actual distance here
        falloff = 1.0 - distanceSquared / radiusSquared;
        falloff *= falloff;

        // A spot only shines within its cone, full inside the inner angle and fading out to the outer one
        if (l.type == LIGHT_SPOT)
        {
            float along = dot(-toLight, l.direction) * rsqrt(max(distanceSquared, 1e-6));
            falloff *= smoothstep(l.cosOuter, l.cosInner, along);
            if (falloff <= 0.0) return 0.0;
        }
    }

    float reach = l.intensity * falloff;
    if (shadows && hasAnything(field) && l.castsShadows > 0.5)
        reach *= shadow(position, position + toLight, l.size + softness, field, l.spriteShadow);

    return reach;
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

    if (lightCount <= 0) return light;
    int tile = tileOf(picture, tileCounts, tileScreen);
    if (tile < 0) return light;

    uint count = min(lightTiles[tile], TILE_LIGHTS);
    for (uint n = 0; n < count; n++)
    {
        Light l = lights[lightTiles[tile + 1 + int(n)]];

        float2 toLight;
        float falloff;
        float reach = lightReach(l, position, shadows, softness, field, toLight, falloff);
        if (reach <= 0.0) continue;

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
