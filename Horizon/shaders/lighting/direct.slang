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
    float uSpritePixelWorld; \
    float uWallHeight; \
    float uSpriteHeight; \
    float uGlowScale;

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
    float wallHeight;        // how tall what blocks light stands off the ground, 0 for taller than any light, see DeferredRenderer2D.WallHeight
    float spriteHeight;      // the same for the sprites that block light
    float2 pictureScale;     // world to picture, picture = world * scale + offset. Orthographic, or this is a lie
    float2 pictureOffset;
};

Field makeField(int hasWalls, float2 origin, float2 cellSize, float2 size, int hasSprites, float spritePixelWorld, int insideShadows, float wallHeight, float spriteHeight)
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
    field.wallHeight = wallHeight;
    field.spriteHeight = spriteHeight;

    // The deferred renderer only ever has a Camera2D looking at it, which is orthographic, so world-to-picture is
    // affine and w is constant; read the scale and offset directly from the row-major view-projection matrix instead
    // of running three full 4x4 multiplies per thread.
    float invW = 0.5 / camera.uViewProjection[3].w;
    field.pictureOffset = camera.uViewProjection[3].xy * invW + 0.5;
    field.pictureScale = float2(camera.uViewProjection[0].x, camera.uViewProjection[1].y) * invW;
    return field;
}

// The field out of a Params that has LIGHTING_PARAMS in it.
#define LIGHTING_FIELD(p) makeField(p.uHasField, p.uFieldOrigin, p.uFieldCellSize, p.uFieldSize, p.uHasSprites, p.uSpritePixelWorld, p.uShadowsInside, p.uWallHeight, p.uSpriteHeight)

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
// And things have a height, if the field says so. Seen from above a lamp hangs over the room, and a fellow stood
// next to it throws a shadow a step long and not one across the whole floor, which is what he did when every light
// was a point in the floor and every wall a wall to the sky. The ray from the lamp down to the point passes over
// whatever stands between at some height, the nearer the point the lower, and only what is taller than that is in
// the way. Which comes out as, a wall of height h only shadows the points within distance * h / lampHeight of
// itself along the ray, so the march towards the lamp need go no further than that from the point, and a wall it
// meets past that is one the light clears. Walls and sprites have a height each, so the houses throw long and the
// people short. A directional light (the sun) says how far a wall throws with its reach and the sprites throw by
// how much lower they are. With no heights given everything is taller than every light, the way it always was.
float shadow(float2 from, float2 to, float size, Field field, float spriteShadow, float lightHeight, bool directional)
{
    float2 way = to - from;
    float distance = length(way);
    if (distance < 0.001) return 1.0;
    float2 direction = way / distance;

    // How far from the point a wall, and a sprite, can be and still stand between it and the light
    float wallsReach = distance, spritesReach = distance;
    if (directional)
    {
        if (field.wallHeight > 0.0 && field.spriteHeight > 0.0) spritesReach = distance * min(field.spriteHeight / field.wallHeight, 1.0);
    }
    else if (lightHeight > 0.0)
    {
        if (field.wallHeight > 0.0 && field.wallHeight < lightHeight) wallsReach = distance * field.wallHeight / lightHeight;
        if (field.spriteHeight > 0.0 && field.spriteHeight < lightHeight) spritesReach = distance * field.spriteHeight / lightHeight;
    }
    float furthest = max(wallsReach, spritesReach);

    // A solid face with interior shadows disabled needs no march out of its own wall.
    bool inWall = wallsDistance(from, field) < 0.0;
    if (inWall && !field.insideShadows) return 1.0;

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
    float visible = 1.0;
    float through = 1.0;
    float previous = 1e9;
    bool sprites = field.hasSprites && !inWall && spriteShadow > 0.0;
    bool inside = false;
    float enteredAt = 0.0;

    float2 wallUvStart = (from - field.origin) * field.invExtent;
    float2 wallUvStep = direction * field.invExtent;
    float2 spriteUvStart = pictureOfFast(from, field);
    float2 spriteUvStep = direction * field.pictureScale;

    for (int step = 0; step < 32; step++)
    {
        if (t >= furthest) break;

        float walls = 1e9;
        if (field.hasWalls)
        {
            float2 wallUv = wallUvStart + wallUvStep * t;
            float distanceField = uTexField.SampleLevel(clamp(wallUv, 0.0, 1.0), 0.0).r;
            float2 outside = max(abs(wallUv - 0.5) - 0.5, 0.0);
            walls = all(outside <= 0.0) ? distanceField : max(distanceField, 0.0) + length(outside * field.extent);
        }
        float d = walls;

        // The nearest thing that is near enough along the way to be tall enough to count, for the penumbra. A
        // wall or a sprite further along than its reach is one the light clears, and no edge to narrow past.
        // Every sprite used to throw a soft wedge the length of the room whatever its hard shadow said, the
        // penumbra never asked how tall it was
        float edge = t + walls <= wallsReach ? walls : 1e9;

        if (sprites)
        {
            // Sprites on their own, which the march goes through, a step at a time while inside of one (and in
            // bigger steps through one that is too far off to be tall enough, it is only being got past)
            float2 picUv = spriteUvStart + spriteUvStep * t;
            float ds = (all(picUv >= 0.0) && all(picUv <= 1.0)) ? uTexSprites.SampleLevel(picUv, 0.0).r : 1e9;
            if (ds < 0.05)
            {
                if (!inside) enteredAt = t;
                inside = true;
                t += max(abs(ds), (t > spritesReach ? 2.0 : 0.5) * texel);
                continue;
            }

            // Out the other side of one, which was in the way after all, if it was near enough to be tall enough
            if (inside)
            {
                inside = false;
                if (enteredAt <= spritesReach)
                {
                    through *= 1.0 - spriteShadow;
                    if (through <= 0.0) return 0.0;
                }
            }

            d = min(d, ds);
            if (t + ds <= spritesReach) edge = min(edge, ds);
        }

        // A wall, in the way if it is near enough for its height, cleared by the light if not
        if (walls < 0.05) return t <= wallsReach ? 0.0 : visible * visible * (3.0 - 2.0 * visible) * through;

        // The nearer the way passes to an edge, the less of a sized light is seen past it. Closing in on one, the
        // last two readings say where the nearest point was, going away from one they say nothing of the sort
        if (size > 0.0 && edge < 1e8)
        {
            float y = edge < previous ? edge * edge / (2.0 * previous) : 0.0;
            float nearest = sqrt(max(edge * edge - y * y, 0.0));
            visible = min(visible, nearest * distance / (size * max(t - surface - y, 0.5)));
            previous = edge;
        }

        t += max(d, 0.5);
    }

    // Still inside of a sprite at the light, the light is behind that one and it throws no shadow from it
    visible = clamp(visible, 0.0, 1.0);
    return visible * visible * (3.0 - 2.0 * visible) * through;
}

float shadow(float2 from, float2 to, float size, Field field, float spriteShadow)
{
    return shadow(from, to, size, field, spriteShadow, 0.0, false);
}

float shadow(float2 from, float2 to, float size, Field field)
{
    return shadow(from, to, size, field, 1.0, 0.0, false);
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
        reach *= shadow(position, position + toLight, l.size + softness, field, l.spriteShadow, l.height, l.type == LIGHT_DIRECTIONAL);

    return reach;
}

// The light every light of a tile throws straight onto a point of the world. What is shiny mirrors the lights
// (highlights, in the light's own colour) and what hangs in the air around a light (glow) come out separately, they
// aren't multiplied by the surface. glowScale is how much of the glow is put down here, 0 when the path tracer
// is doing the air instead (see gi_air.slang), or a lamp would hang in the air twice over.
float3 directLight(
    float2 position, float2 picture, float3 normal, bool hasNormal, float shine,
    int lightCount, bool shadows, float shininess, float softness, float glowScale,
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
        glow += l.color * (reach * falloff * l.glow * glowScale);

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

// How open to the ambient light a point of the world is, 1 for wide open, down to 1 minus the strength in a corner.
// Ambient light comes from everywhere, so what is hemmed in by walls and sprites gets less of it, the corner where
// the floor meets a wall, the ground under a fighter's feet, the air right next to a pillar. A few rays are
// marched a short way through the distance field, every one that meets something within the radius takes a share
// of the light, the sooner it meets it the bigger the share. A point inside a wall is its face, the rays start
// just outside and only go the way the face looks, a flat floor is wide open and its corners are not, and the
// deeper in the point is the less of that it takes, so what a foot or a crate does to the floor fades out on the
// way down instead of standing in it as a bar. Only the walls say where the face is, a sprite is a picture in front
// of the world and has no inside to speak of, and a pixel that is such a sprite doesn't count the sprites at all
// (sprites false), or every fighter is hemmed in by themselves and comes out speckled like a fucking dalmatian.
float ambientOcclusion(float2 position, Field field, float radius, int samples, float strength, bool sprites)
{
    if (!hasAnything(field) || samples <= 0 || radius <= 0.0 || strength <= 0.0) return 1.0;

    float texel = fieldTexel(field);
    float2 at = position;
    float2 baseDir = float2(1.0, 0.0);
    float span = 6.2831853;
    float share = 1.0;

    float d = wallsDistance(at, field);

    // A distance field bounds every direction at once. Leave a two-texel margin for the
    // sampled field, then empty space needs no rays or trigonometry at all.
    float margin = 2.0 * max(max(field.cellSize.x, field.cellSize.y), field.spritePixelWorld);
    if (d > radius + margin && (!sprites || spritesDistance(at, field, false) > radius + margin))
        return 1.0;

    if (d < texel)
    {
        if (-d >= radius) return 1.0;

        float2 slope = float2(
            wallsDistance(at + float2(texel, 0.0), field) - wallsDistance(at - float2(texel, 0.0), field),
            wallsDistance(at + float2(0.0, texel), field) - wallsDistance(at - float2(0.0, texel), field));
        if (dot(slope, slope) < 1e-8) return 1.0;

        float2 outward = normalize(slope);
        if (d < 0.0)
        {
            at += outward * (-d + 0.5 * texel);

            // Next to a ridge of the field (under a crate that stands on the floor, the middle of a box) the slope
            // points somewhere near the face and not at it, and whether that lands in the open or not changes from
            // one pixel to the next, which is noise. Once more from where it landed and it is out
            float still = wallsDistance(at, field);
            if (still < 0.0)
            {
                at += outward * (-still + 0.5 * texel);

                // Still in, which is the ridge itself, there is no face to find from here and it is left open
                if (wallsDistance(at, field) < 0.0) return 1.0;
            }

            share = 1.0 - clamp(-d / radius, 0.0, 1.0);
        }

        // The half of the directions the face looks towards: outward rotated by -pi/2 is (outward.y, -outward.x)
        baseDir = float2(outward.y, -outward.x);
        span = 3.1415927;
    }

    // One sincos for the half-step angle, then double-angle for the full step and iterative 2D rotation per sample
    float halfStep = 0.5 * span / float(samples);
    float sinHalf, cosHalf;
    sincos(halfStep, sinHalf, cosHalf);
    float cosStep = cosHalf * cosHalf - sinHalf * sinHalf;
    float sinStep = 2.0 * sinHalf * cosHalf;
    float2 direction = float2(
        baseDir.x * cosHalf - baseDir.y * sinHalf,
        baseDir.x * sinHalf + baseDir.y * cosHalf);

    float occluded = 0.0;
    for (int i = 0; i < samples; i++)
    {
        float t = 0.5 * texel;
        float hit = radius;
        for (int step = 0; step < 4; step++)
        {
            float2 ahead = at + direction * t;
            float distance = sprites ? wallDistance(ahead, field) : wallsDistance(ahead, field);
            if (distance < 0.05)
            {
                hit = t;
                break;
            }

            t += max(distance, 0.5 * texel);
            if (t >= radius) break;
        }

        occluded += 1.0 - hit / radius;
        direction = float2(
            direction.x * cosStep - direction.y * sinStep,
            direction.x * sinStep + direction.y * cosStep);
    }

    return 1.0 - strength * share * occluded / float(samples);
}

// Computes both (aoWithSprites, aoWithoutSprites) sharing the wall-exit gradient and angular sincos setup.
// Skips sprite work entirely when no sprite field is active or the nearest sprite is beyond radius + margin.
float2 ambientOcclusionPair(float2 position, Field field, float radius, int samples, float strength)
{
    if (!hasAnything(field) || samples <= 0 || radius <= 0.0 || strength <= 0.0) return float2(1.0, 1.0);

    float texel = fieldTexel(field);
    float2 at = position;
    float2 baseDir = float2(1.0, 0.0);
    float span = 6.2831853;
    float share = 1.0;

    float d = wallsDistance(at, field);
    float margin = 2.0 * max(max(field.cellSize.x, field.cellSize.y), field.spritePixelWorld);
    bool wallsNear = d <= radius + margin;
    bool spritesNear = field.hasSprites && spritesDistance(at, field, false) <= radius + margin;

    if (!wallsNear && !spritesNear) return float2(1.0, 1.0);

    if (d < texel)
    {
        if (-d >= radius) return float2(1.0, 1.0);

        float2 slope = float2(
            wallsDistance(at + float2(texel, 0.0), field) - wallsDistance(at - float2(texel, 0.0), field),
            wallsDistance(at + float2(0.0, texel), field) - wallsDistance(at - float2(0.0, texel), field));
        if (dot(slope, slope) < 1e-8) return float2(1.0, 1.0);

        float2 outward = normalize(slope);
        if (d < 0.0)
        {
            at += outward * (-d + 0.5 * texel);
            float still = wallsDistance(at, field);
            if (still < 0.0)
            {
                at += outward * (-still + 0.5 * texel);
                if (wallsDistance(at, field) < 0.0) return float2(1.0, 1.0);
            }
            share = 1.0 - clamp(-d / radius, 0.0, 1.0);
        }

        baseDir = float2(outward.y, -outward.x);
        span = 3.1415927;
    }

    float halfStep = 0.5 * span / float(samples);
    float sinHalf, cosHalf;
    sincos(halfStep, sinHalf, cosHalf);
    float cosStep = cosHalf * cosHalf - sinHalf * sinHalf;
    float sinStep = 2.0 * sinHalf * cosHalf;
    float2 direction = float2(
        baseDir.x * cosHalf - baseDir.y * sinHalf,
        baseDir.x * sinHalf + baseDir.y * cosHalf);

    float occludedWith = 0.0;
    float occludedWithout = 0.0;
    for (int i = 0; i < samples; i++)
    {
        if (spritesNear)
        {
            float t = 0.5 * texel;
            float hitWith = radius;
            for (int step = 0; step < 4; step++)
            {
                float2 ahead = at + direction * t;
                float distance = wallsNear ? wallDistance(ahead, field) : spritesDistance(ahead, field, false);
                if (distance < 0.05) { hitWith = t; break; }
                t += max(distance, 0.5 * texel);
                if (t >= radius) break;
            }
            occludedWith += 1.0 - hitWith / radius;
        }

        if (wallsNear)
        {
            float tw = 0.5 * texel;
            float hitWithout = radius;
            for (int step = 0; step < 4; step++)
            {
                float2 ahead = at + direction * tw;
                float distance = wallsDistance(ahead, field);
                if (distance < 0.05) { hitWithout = tw; break; }
                tw += max(distance, 0.5 * texel);
                if (tw >= radius) break;
            }
            occludedWithout += 1.0 - hitWithout / radius;
        }

        direction = float2(
            direction.x * cosStep - direction.y * sinStep,
            direction.x * sinStep + direction.y * cosStep);
    }

    float scale = strength * share / float(samples);
    float aoWithout = 1.0 - scale * occludedWithout;
    float aoWith = spritesNear ? (1.0 - scale * occludedWith) : aoWithout;
    return float2(aoWith, aoWithout);
}
