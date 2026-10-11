// The bounce light kept in the world rather than on the screen. A hash table on the GPU, one entry for every face of
// a block (bigger cells further off, two, four, eight blocks across, by how big a pixel is there), holding how much
// light has bounced onto that face. Every surface the eye sees adds what the bounce light found for it to its cell,
// and a bounce ray that lands somewhere reads the cell it landed in for the light that bounced there before, which
// is how one ray a pixel is every bounce and not one.
//
// It used to read last frame's picture for that instead, which only knows what was on screen. Turn and a wall that
// had been lit by the room behind it went over to a guess the moment that room was off screen, and the light on half
// the scene pumped up and down as the camera panned. The cache doesn't care where the camera is, and it remembers.
//
// The first version of it flickered once a world had been walked about in for a while, three ways, and this one
// doesn't. Entries never went away, so the table filled up and two faces that hashed to the same slot wrote over
// each other every frame. Now an entry has an age, a slot is probed four along, and a face takes over a slot only
// when it is empty or hasn't been written for a few hundred frames. Every pixel of a face wrote its own value into
// the cell and the last one won, so the cell jumped about by which pixel came last. Now the writes add up (atomics,
// fixed point) and a reader takes the mean, halved every so often so it follows a change. And a cell is picked by
// how far off the face is, so walking towards something moved it to another cell that was empty for a frame. Now a
// reader that finds nothing at its cell tries the cell sizes either side of it.
//
// An entry is eight numbers. The check (so a face that hashes to the same slot isn't taken for this one), how many
// writes are in it, the frame it was last written, a spare, and the sums of red, green and blue in 1/4096ths.

#include <voxels/frame.slang>

BIND_BUFFER(8) RWStructuredBuffer<uint> gRadianceCache;

static const uint CACHE_SIZE = 1u << 20;
static const uint CACHE_STRIDE = 8u;
static const uint CACHE_PROBES = 4u;

// Writes add up in these, halved once a cell has this many so it keeps up with a change
static const float CACHE_SCALE = 4096.0;
static const uint CACHE_HALVE_AT = 96u;

// A cell nobody has written for this long is anybody's, and one this old isn't believed
static const uint CACHE_STALE = 400u;
static const uint CACHE_FORGET = 900u;

// Which cell size a face is kept at from this far off, the cell is 1 << level blocks
int cacheLevel(float distance)
{
    FrameData f = frame();
    return clamp(int(floor(log2(max(distance * f.lod.x * 6.0, 1.0)))), 0, 7);
}

// Where a face's entry starts and what its check is, at a cell size
uint2 cacheSlot(float3 position, float3 normal, int level)
{
    float size = float(1 << level);

    // The face it's on, by the way it faces, and the cell it's in, a hair inside the block so a face and the one
    // across the air from it aren't the same cell
    uint face = abs(normal.x) > 0.5 ? (normal.x > 0.0 ? 0u : 1u) : abs(normal.y) > 0.5 ? (normal.y > 0.0 ? 2u : 3u) : (normal.z > 0.0 ? 4u : 5u);
    int3 cell = int3(floor((position - normal * 0.02) / size));

    uint h = hash3u(cell) ^ (face * 0x9E3779B9u) ^ (uint(level) * 0x85EBCA6Bu);
    uint check = pcg(h ^ 0x27d4eb2du) | 1u;
    return uint2(h & (CACHE_SIZE - 1u), check);
}

// The mean of a cell, if it has one worth having
bool cacheReadLevel(float3 position, float3 normal, int level, uint now, out float3 light)
{
    uint2 slot = cacheSlot(position, normal, level);
    for (uint probe = 0u; probe < CACHE_PROBES; probe++)
    {
        uint at = ((slot.x + probe) & (CACHE_SIZE - 1u)) * CACHE_STRIDE;
        uint check = gRadianceCache[at];
        if (check == 0u) break;
        if (check != slot.y) continue;

        uint count = gRadianceCache[at + 1u];
        uint age = gRadianceCache[at + 2u];
        if (count == 0u || now - age > CACHE_FORGET) break;

        light = float3(gRadianceCache[at + 4u], gRadianceCache[at + 5u], gRadianceCache[at + 6u]) / (CACHE_SCALE * float(count));
        return true;
    }

    light = float3(0.0, 0.0, 0.0);
    return false;
}

// The light that bounced onto a face, if the cache has it. Its own cell size first, then the one up and the one down,
// which is where it was kept a moment ago if the camera has moved since
bool cacheRead(float3 position, float3 normal, out float3 light)
{
    FrameData f = frame();
    uint now = uint(f.prevCameraPos.w);
    int level = cacheLevel(length(position - f.cameraPos.xyz));

    if (cacheReadLevel(position, normal, level, now, light)) return true;
    if (level < 7 && cacheReadLevel(position, normal, level + 1, now, light)) return true;
    if (level > 0 && cacheReadLevel(position, normal, level - 1, now, light)) return true;
    return false;
}

// Adds what was found for a face to its cell, taking a slot if it has none
void cacheWrite(float3 position, float3 normal, float3 light)
{
    FrameData f = frame();
    uint now = uint(f.prevCameraPos.w);
    int level = cacheLevel(length(position - f.cameraPos.xyz));
    uint2 slot = cacheSlot(position, normal, level);

    for (uint probe = 0u; probe < CACHE_PROBES; probe++)
    {
        uint at = ((slot.x + probe) & (CACHE_SIZE - 1u)) * CACHE_STRIDE;
        uint check = gRadianceCache[at];

        if (check != slot.y)
        {
            // Somebody else's, unless it's empty or they haven't been back in a while, then it's ours if we get there first
            bool free = check == 0u || now - gRadianceCache[at + 2u] > CACHE_STALE;
            if (!free) continue;

            uint was;
            InterlockedCompareExchange(gRadianceCache[at], check, slot.y, was);
            if (was != check && was != slot.y) continue;

            if (was == check)
            {
                // Taken over, from nothing
                gRadianceCache[at + 1u] = 0u;
                gRadianceCache[at + 4u] = 0u;
                gRadianceCache[at + 5u] = 0u;
                gRadianceCache[at + 6u] = 0u;
            }
        }

        uint3 fixed = uint3(saturate(light / 64.0) * 64.0 * CACHE_SCALE);
        uint count;
        InterlockedAdd(gRadianceCache[at + 1u], 1u, count);
        InterlockedAdd(gRadianceCache[at + 4u], fixed.x);
        InterlockedAdd(gRadianceCache[at + 5u], fixed.y);
        InterlockedAdd(gRadianceCache[at + 6u], fixed.z);
        InterlockedMax(gRadianceCache[at + 2u], now);

        // Full enough, half of it goes so the newest writes count for more. Racy and harmless, the odd write is lost
        if (count >= CACHE_HALVE_AT)
        {
            gRadianceCache[at + 1u] = count / 2u;
            gRadianceCache[at + 4u] = gRadianceCache[at + 4u] / 2u;
            gRadianceCache[at + 5u] = gRadianceCache[at + 5u] / 2u;
            gRadianceCache[at + 6u] = gRadianceCache[at + 6u] / 2u;
        }

        return;
    }
}
