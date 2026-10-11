// Going through the world. The sparse 64-tree (VoxelTree.cs has the long version of why), its materials, the little
// models foliage is made of, and the walk a ray takes through all of it. Every pass that traces includes this.
//
// A node is a uint4. xy the mask of which of its 4 x 4 x 4 children are there (bit x + z*4 + y*16), z where the
// children start (or the material, for a node that is one material all the way through), w the colour from far
// away in the bottom three bytes and the flags on top. The children of a node sit together and only the ones that
// are there, so the place of child n is the start plus how many bits come before n, one countbits. The bottom
// nodes have voxels for children, one byte each, four to a uint.

#include <voxels/frame.slang>

BIND_BUFFER(2) StructuredBuffer<uint4> gNodes;
BIND_BUFFER(3) StructuredBuffer<uint> gVoxels;
BIND_BUFFER(4) StructuredBuffer<uint4> gMaterials;
BIND_BUFFER(5) StructuredBuffer<uint> gModels;

static const uint NODE_LEAF = 1u << 24;
static const uint NODE_UNIFORM = 1u << 25;
static const uint NODE_LIQUID = 1u << 26;
static const uint NODE_GLOW = 1u << 27;
static const uint NODE_SOFT = 1u << 28;

static const uint KIND_AIR = 0;
static const uint KIND_SOLID = 1;
static const uint KIND_LIQUID = 2;
static const uint KIND_FOLIAGE = 3;
static const uint KIND_LEAVES = 4;
static const uint KIND_GLASS = 5;

// What a trace is for
static const uint TRACE_STOP_AT_WATER = 1u;  // the eye ray, which has a water surface to draw. Everything else goes through
static const uint TRACE_SHADOW = 2u;         // anything solid ends it, nobody cares what it was
static const uint TRACE_COARSE = 4u;         // the bounce light's rays, which go through foliage and take leaves as solid. Nobody
                                             // can see the shadow a tuft of grass throws in light that has bounced

static const int MODEL_SIZE = 8;
static const int MODEL_UINTS = 36;
static const int LEAF_CELLS = 4;

// How far into a voxel the holes in leaves go before they count as solid from a distance, in voxels a pixel
static const float LEAVES_SOLID_FOOTPRINT = 0.3;

// And how far off foliage starts thinning out, and where it is gone, a pixel being this many voxels
static const float FOLIAGE_THIN = 0.12;
static const float FOLIAGE_GONE = 0.45;

// How much light a voxel of water takes out, per colour, per voxel of it gone through. Red goes first, like the sea
static const float3 WATER_ABSORPTION = float3(0.26, 0.07, 0.045);

struct Material
{
    uint kind;
    float3 color;
    uint top, side, bottom;
    uint model;
    float3 emission;
    float roughness;
    bool sways;
};

Material material(uint m)
{
    uint4 r = gMaterials[m];
    Material o;
    o.kind = r.x >> 24;
    o.color = unpackColor(r.x);
    o.top = r.y & 0xFF;
    o.side = (r.y >> 8) & 0xFF;
    o.bottom = (r.y >> 16) & 0xFF;
    o.model = r.y >> 24;
    o.emission = float3(f16tof32(r.z & 0xFFFF), f16tof32(r.z >> 16), f16tof32(r.w & 0xFFFF));
    o.roughness = float((r.w >> 16) & 0xFF) / 255.0;
    o.sways = ((r.w >> 24) & 1u) != 0;
    return o;
}

uint materialKind(uint m) { return gMaterials[m].x >> 24; }

uint voxelByte(uint i) { return (gVoxels[i >> 2] >> ((i & 3u) * 8u)) & 0xFFu; }

bool hasChild(uint4 n, uint bit)
{
    return bit < 32u ? ((n.x >> bit) & 1u) != 0u : ((n.y >> (bit - 32u)) & 1u) != 0u;
}

uint childRank(uint4 n, uint bit)
{
    return bit < 32u
        ? countbits(n.x & ((1u << bit) - 1u))
        : countbits(n.x) + countbits(n.y & ((1u << (bit - 32u)) - 1u));
}

// What a ray found
struct Hit
{
    bool hit;
    float t;            // how far along the ray
    float3 normal;      // the face it came in through
    int3 voxel;         // which voxel (for a far away node, the corner of it)
    uint material;      // 0 for a far away node, which only has a colour
    float3 color;       // the colour where nothing better is known (far away, a model, leaves)
    uint kind;
    bool far;           // stopped at a node smaller than its pixel, not a voxel
    bool micro;         // landed on a model or a leaf, the colour is all there is
    float3 transmit;    // what the light lost on the way (water, glass), 1 for nothing
    float cell;         // how big what it hit is, in voxels
    int steps;          // how many steps it took, for the debug view
};

Hit noHit()
{
    Hit h = (Hit)0;
    h.transmit = float3(1.0, 1.0, 1.0);
    h.cell = 1.0;
    return h;
}

float3 axisNormal(int axis, float3 d)
{
    float3 n = float3(0.0, 0.0, 0.0);
    if (axis == 0) n.x = d.x > 0.0 ? -1.0 : 1.0;
    else if (axis == 1) n.y = d.y > 0.0 ? -1.0 : 1.0;
    else if (axis == 2) n.z = d.z > 0.0 ? -1.0 : 1.0;
    else n = -d;
    return n;
}

/* The little things inside one voxel, a model of 8 a side or the holes in leaves (4 a side), walked the same way the
   world is walked, a cell at a time. Starts where the ray came into the voxel and goes until it leaves it */

// A model voxel, 0 for nothing or 1 to 3 for its colour, turned round by `turn` quarters and leant over by the wind
uint modelCell(uint model, int3 c, uint turn, float lean)
{
    int x = c.x, z = c.z;
    if (turn == 1u) { x = MODEL_SIZE - 1 - c.z; z = c.x; }
    else if (turn == 2u) { x = MODEL_SIZE - 1 - c.x; z = MODEL_SIZE - 1 - c.z; }
    else if (turn == 3u) { x = c.z; z = MODEL_SIZE - 1 - c.x; }

    // Leant over by the wind, the top more than the bottom
    x -= int(round(lean * float(c.y) / float(MODEL_SIZE - 1)));
    if (x < 0 || x >= MODEL_SIZE) return 0u;

    uint index = uint(x + z * MODEL_SIZE + c.y * MODEL_SIZE * MODEL_SIZE);
    uint word = gModels[model * MODEL_UINTS + (index >> 4)];
    return (word >> ((index & 15u) * 2u)) & 3u;
}

// A leaf voxel's cell, solid or a hole, from where it is
uint leafCell(int3 voxel, int3 c)
{
    uint h = hash3u(voxel * LEAF_CELLS + c);
    return (h & 255u) < 150u ? 1u + (h >> 8) % 2u : 0u;
}

bool marchInside(float3 o, float3 d, float3 inv, int3 voxel, float t0, float t1, int axis, uint mode, uint model,
                 uint turn, float lean, inout Hit h)
{
    int R = mode == 0u ? MODEL_SIZE : LEAF_CELLS;
    float fR = float(R);

    float3 local = clamp((o + d * t0 - float3(voxel)) * fR, 0.0005, fR - 0.0005);
    int3 c = int3(floor(local));
    int3 stepv = int3(d.x > 0.0 ? 1 : -1, d.y > 0.0 ? 1 : -1, d.z > 0.0 ? 1 : -1);
    float3 delta = abs(inv) / fR;
    float3 boundary = (float3(c) + select(d > 0.0, 1.0, 0.0)) / fR + float3(voxel);
    float3 next = (boundary - o) * inv;
    float t = t0;

    for (int k = 0; k < 3 * MODEL_SIZE; k++)
    {
        uint v = mode == 0u ? modelCell(model, c, turn, lean) : leafCell(voxel, c);
        if (v != 0u)
        {
            h.hit = true;
            h.t = t;
            h.normal = axisNormal(axis, d);
            h.voxel = voxel;
            h.micro = true;
            h.cell = 1.0 / fR;

            if (mode == 0u)
            {
                h.color = unpackColor(gModels[model * MODEL_UINTS + 32 + (v - 1u)]);
            }
            else
            {
                // Leaves aren't all the same green, a bit lighter or darker leaf by leaf
                float shade = 0.82 + 0.3 * float(hash3u(voxel * LEAF_CELLS + c) & 63u) / 63.0;
                h.color *= shade;
            }

            return true;
        }

        if (next.x < next.y && next.x < next.z) { c.x += stepv.x; t = next.x; next.x += delta.x; axis = 0; }
        else if (next.y < next.z) { c.y += stepv.y; t = next.y; next.y += delta.y; axis = 1; }
        else { c.z += stepv.z; t = next.z; next.z += delta.z; axis = 2; }

        if (t > t1 || any(c < 0) || any(c >= R)) return false;
    }

    return false;
}

// How far the ray goes through a cell before it leaves it
float exitOf(float3 o, float3 inv, float3 d, int3 cellMin, float size)
{
    float3 bound = float3(cellMin) + select(d > 0.0, size, 0.0);
    float3 tt = (bound - o) * inv;
    return min(tt.x, min(tt.y, tt.z));
}

// Which quarter turn a block of foliage has, and how far the wind leans it right now (in cells of its model)
void foliagePose(int3 voxel, bool sways, out uint turn, out float lean)
{
    uint h = hash3u(voxel);
    turn = h & 3u;
    lean = 0.0;
    if (sways)
    {
        FrameData f = frame();
        float phase = float((h >> 2) & 1023u) / 1023.0 * 6.2831;
        float gust = sin(f.cameraPos.w * 1.3 + float(voxel.x) * 0.08 + float(voxel.z) * 0.05);
        lean = f.misc.y * (0.55 * sin(f.cameraPos.w * 2.1 + phase) + 0.45 * gust) * 1.6;
    }
}

/// <summary>
/// What a voxel (or a cell of one material) does to the ray, given the ray is in it from t. True if the ray ends here
/// (h is filled in), false if it goes on through (h.transmit may have lost some light). Air never gets this far.
/// Solid is by far the most common and is answered off the first number of the material, nothing else is read,
/// and where the ray leaves the cell is only worked out for what the ray goes into (water, leaves, foliage).
/// </summary>
bool landOn(uint m, int3 voxel, int3 cellMin, float size, float3 o, float3 d, float3 inv, float t, int axis, float footprint,
            uint flags, inout Hit h)
{
    uint4 r = gMaterials[m];
    uint kind = r.x >> 24;

    if (kind == KIND_SOLID)
    {
        h.hit = true; h.t = t; h.normal = axisNormal(axis, d); h.voxel = voxel; h.material = m;
        h.color = unpackColor(r.x); h.kind = KIND_SOLID; h.cell = size;
        return true;
    }

    if (kind == KIND_LIQUID)
    {
        if ((flags & TRACE_STOP_AT_WATER) != 0u)
        {
            h.hit = true; h.t = t; h.normal = axisNormal(axis, d); h.voxel = voxel; h.material = m;
            h.color = unpackColor(r.x); h.kind = KIND_LIQUID; h.cell = size;
            return true;
        }

        // Through it, losing a bit of light for every voxel of it
        h.transmit *= exp(-WATER_ABSORPTION * max(exitOf(o, inv, d, cellMin, size) - t, 0.0));
        return false;
    }

    if (kind == KIND_GLASS)
    {
        h.transmit *= lerp(float3(1.0, 1.0, 1.0), unpackColor(r.x), 0.6) * 0.92;
        return false;
    }

    if (kind == KIND_LEAVES)
    {
        // Far enough off the holes are smaller than a pixel and it may as well be solid
        if (footprint > LEAVES_SOLID_FOOTPRINT || (flags & TRACE_COARSE) != 0u || (flags & TRACE_SHADOW) != 0u && footprint > LEAVES_SOLID_FOOTPRINT * 0.5)
        {
            h.hit = true; h.t = t; h.normal = axisNormal(axis, d); h.voxel = voxel; h.material = m;
            h.color = unpackColor(r.x); h.kind = KIND_LEAVES; h.cell = size;
            return true;
        }

        h.color = unpackColor(r.x);
        if (marchInside(o, d, inv, voxel, t, exitOf(o, inv, d, cellMin, size), axis, 1u, 0u, 0u, 0.0, h))
        {
            h.material = m; h.kind = KIND_LEAVES;
            return true;
        }

        h.color = float3(0.0, 0.0, 0.0);
        return false;
    }

    if (kind == KIND_FOLIAGE)
    {
        // Thinned out with distance, a few of them at a time so there is no line where the grass stops
        if (footprint > FOLIAGE_GONE || (flags & TRACE_COARSE) != 0u) return false;
        if (footprint > FOLIAGE_THIN && hash3(voxel + int3(17, 0, 3)) < (footprint - FOLIAGE_THIN) / (FOLIAGE_GONE - FOLIAGE_THIN)) return false;

        uint turn; float lean;
        foliagePose(voxel, ((r.w >> 24) & 1u) != 0u, turn, lean);
        if (marchInside(o, d, inv, voxel, t, exitOf(o, inv, d, cellMin, size), axis, 0u, r.y >> 24, turn, lean, h))
        {
            h.material = m; h.kind = KIND_FOLIAGE;
            return true;
        }

        return false;
    }

    return false;
}

/// <summary>
/// Sends a ray through the world and says what it hit. o and d in voxels (d a unit vector), from tMin to tMax along
/// it. The ray stops going down the tree once a node is smaller than footprint(t) = lodBase + lodSlope * t, which
/// is how big a pixel is that far off (or a cone, for the bounce light), and takes that node's colour from far away.
/// That's the level of detail and it is why the whole world can be on screen at once.
/// <para>
/// The walk. The ray is in a cell of the node it's in (ip, the voxel it's at, picks the cell). A cell with something
/// in it is gone down into, unless it's small enough to stop at. An empty one is stepped over whole, to where the ray
/// leaves it, which at the top of the tree is hundreds of voxels in one step. Stepped out of the node it's in, it
/// climbs back up to the lowest node that has both the old voxel and the new, which is the highest bit that differs
/// between the two, halved (each level is two bits of the coordinate). The node in hand is kept in registers until
/// the ray leaves it, a cell of the same node is one mask test and no reads.
/// </para>
/// </summary>
Hit trace(float3 o, float3 d, float tMin, float tMax, float lodBase, float lodSlope, uint flags, int maxSteps)
{
    Hit h = noHit();
    FrameData f = frame();
    float3 size = f.world.xyz;
    int3 isize = int3(size);
    int depth = int(f.world.w);

    // No zeros, a ray going exactly along an axis would divide by them
    d = select(abs(d) < 1e-7, select(d < 0.0, -1e-7, 1e-7), d);
    float3 inv = 1.0 / d;
    bool3 positive = d > 0.0;

    // Into the box of the world, or not at all
    float3 ta = -o * inv;
    float3 tb = (size - o) * inv;
    float3 tn = min(ta, tb), tf = max(ta, tb);
    float enter = max(max(tn.x, tn.y), max(tn.z, tMin));
    float leave = min(min(tf.x, tf.y), min(tf.z, tMax));
    if (enter >= leave) return h;

    int axis = -1;
    if (enter > tMin)
        axis = enter == tn.x ? 0 : (enter == tn.y ? 1 : 2);

    float t = enter;
    float3 p = o + d * t;
    int3 ip = int3(floor(p));
    if (axis >= 0) ip[axis] = positive[axis] ? int(round(p[axis])) : int(round(p[axis])) - 1;
    ip = clamp(ip, int3(0, 0, 0), isize - 1);

    uint stack[10];
    int level = depth;
    uint node = 0u;
    stack[depth] = 0u;
    uint4 n = gNodes[0];

    for (int i = 0; i < maxSteps; i++)
    {
        h.steps = i;
        int shift = 2 * (level - 1);
        int3 c = (ip >> shift) & 3;
        uint bit = uint(c.x | (c.z << 2) | (c.y << 4));
        int cellSize = 1 << shift;
        int3 cellMin = (ip >> shift) << shift;

        if (hasChild(n, bit))
        {
            uint index = n.z + childRank(n, bit);
            float footprint = lodBase + lodSlope * t;

            if ((n.w & NODE_LEAF) != 0u)
            {
                if (landOn(voxelByte(index), ip, ip, 1.0, o, d, inv, t, axis, footprint, flags, h)) return h;
                if ((flags & TRACE_SHADOW) != 0u && max(h.transmit.x, max(h.transmit.y, h.transmit.z)) < 0.01)
                {
                    h.hit = true; h.t = t;
                    return h;
                }
            }
            else
            {
                uint4 child = gNodes[index];

                if ((child.w & NODE_UNIFORM) != 0u)
                {
                    // One material all the way through, the whole cell is one big voxel of it
                    if (landOn(child.z, ip, cellMin, float(cellSize), o, d, inv, t, axis, footprint, flags, h)) return h;
                }
                else if (float(cellSize) <= footprint)
                {
                    // Smaller than its pixel, it's a block of its colour, unless it's only foliage, which is nothing from here.
                    // Water from far off is water, so the sea still shines at the horizon
                    if ((child.w & NODE_SOFT) == 0u)
                    {
                        bool wet = (child.w & NODE_LIQUID) != 0u;
                        if (!wet || (flags & TRACE_STOP_AT_WATER) != 0u)
                        {
                            h.hit = true; h.t = t; h.normal = axisNormal(axis, d); h.voxel = cellMin;
                            h.color = unpackColor(child.w); h.far = true; h.cell = float(cellSize);
                            h.kind = wet ? KIND_LIQUID : KIND_SOLID;
                            return h;
                        }

                        h.transmit *= exp(-WATER_ABSORPTION * max(exitOf(o, inv, d, cellMin, float(cellSize)) - t, 0.0));
                    }
                }
                else
                {
                    level--;
                    node = index;
                    stack[level] = node;
                    n = child;
                    continue;
                }
            }
        }

        // Out of this cell, whatever size it is at this level, empty cells are skipped whole
        float3 bound = float3(cellMin + select(positive, cellSize, 0));
        float3 tt = (bound - o) * inv;
        int3 before = ip;

        if (tt.x <= tt.y && tt.x <= tt.z) { axis = 0; t = max(t, tt.x); }
        else if (tt.y <= tt.z) { axis = 1; t = max(t, tt.y); }
        else { axis = 2; t = max(t, tt.z); }

        if (t >= leave) return h;

        p = o + d * t;
        ip = clamp(int3(floor(p)), cellMin, cellMin + cellSize - 1);
        ip[axis] = positive[axis] ? cellMin[axis] + cellSize : cellMin[axis] - 1;
        if (any(ip < 0) || any(ip >= isize)) return h;

        // Back up the tree to the lowest node that has both where it was and where it is now
        int3 changed = ip ^ before;
        int need = int(firstbithigh(uint(changed.x | changed.y | changed.z))) / 2 + 1;
        if (need > level)
        {
            if (need > depth) return h;
            level = need;
            node = stack[level];
            n = gNodes[node];
        }
    }

    return h;
}

/// <summary>How much light gets from a point to a direction a long way off (the sun), 0 to 1 a colour.</summary>
float3 shadow(float3 o, float3 d, float lodBase, float lodSlope, int maxSteps, float reach = 1536.0, uint extra = 0u)
{
    Hit h = trace(o, d, 0.0, reach, lodBase, lodSlope, TRACE_SHADOW | extra, maxSteps);
    return h.hit ? float3(0.0, 0.0, 0.0) : h.transmit;
}

/* What a surface looks like */

BIND_TEXTURE(7) Sampler2D uAtlas;

/// <summary>
/// The colour of a surface that was hit, in linear light. The tile of the atlas for the face it is, fading to the
/// material's average as a texel gets smaller than a pixel (or it would sparkle), a bit lighter or darker block by
/// block so a wall of stone isn't wallpaper. Far away nodes and the little things already have their colour.
/// </summary>
float3 surfaceColor(Hit h, float3 position, float footprint)
{
    if (h.far || h.material == 0u || h.micro && h.kind != KIND_LEAVES) return h.color;

    Material mat = material(h.material);
    FrameData f = frame();
    float3 color = mat.color;

    // A leaf's cell is a quarter of a block, lighter or darker than the next, with the tile's pattern on it
    float3 shade = h.micro ? h.color / max(mat.color, 1e-3) : float3(1.0, 1.0, 1.0);

    uint tile = h.normal.y > 0.5 ? mat.top : (h.normal.y < -0.5 ? mat.bottom : mat.side);
    float texels = f.atlas.w;
    float fade = saturate((footprint * texels - 0.7) / 2.5);

    if (tile != 255u && f.atlas.z > 0.5 && fade < 1.0)
    {
        float3 inside = position - float3(h.voxel);
        float2 uv;
        if (abs(h.normal.y) > 0.5) uv = inside.xz;
        else if (abs(h.normal.x) > 0.5) uv = float2(h.normal.x > 0.0 ? 1.0 - inside.z : inside.z, 1.0 - inside.y);
        else uv = float2(h.normal.z > 0.0 ? inside.x : 1.0 - inside.x, 1.0 - inside.y);

        uv = clamp(uv, 0.001, 0.999);
        float2 cell = float2(float(tile % uint(f.atlas.x)), float(tile / uint(f.atlas.x)));
        float3 texel = uAtlas.SampleLevel((cell + uv) * f.atlas.y, 0.0).rgb;
        color = lerp(texel, mat.color, fade);
    }

    // No two blocks quite the same
    float jitter = 0.94 + 0.12 * hash3(h.voxel);
    return color * jitter * shade;
}

/// <summary>
/// The light a surface gives off by itself. With its colour, the pattern of its tile shows in the glow, lava has
/// its swirls and glowstone its cells, and doesn't come out as one flat blown out blob.
/// </summary>
float3 surfaceEmission(Hit h, float3 albedo)
{
    if (h.material == 0u) return float3(0.0, 0.0, 0.0);
    Material mat = material(h.material);
    if (dot(mat.emission, mat.emission) <= 0.0) return float3(0.0, 0.0, 0.0);
    return mat.emission * clamp(albedo / max(mat.color, 0.02), 0.25, 2.5);
}

/// <summary>The same, when there's no colour to hand (a bounce ray), a far away node glows at its average.</summary>
float3 surfaceEmission(Hit h)
{
    if (h.material == 0u) return float3(0.0, 0.0, 0.0);
    return material(h.material).emission;
}

/* What the eye rays hit, packed into four numbers so the pass that shades it doesn't have to trace it again. The eye
   rays are a pass of their own because a pass with four traces in it (the eye, the shadow, the reflection and what is
   under the water) needs so many registers that the GPU can only keep a few of them going at once, measured, the eye
   rays on their own were 60% quicker */

// x the voxel's x and z, y its y, material, kind, flags and face, z how big it was and what the light lost on the way, w its colour
uint4 packHit(Hit h)
{
    uint face = 6u;
    if (abs(h.normal.x) > 0.5) face = h.normal.x > 0.0 ? 0u : 1u;
    else if (abs(h.normal.y) > 0.5) face = h.normal.y > 0.0 ? 2u : 3u;
    else if (abs(h.normal.z) > 0.5) face = h.normal.z > 0.0 ? 4u : 5u;

    uint flags = (h.far ? 1u : 0u) | (h.micro ? 2u : 0u) | (h.hit ? 4u : 0u);
    int cellLog = clamp(int(round(log2(max(h.cell, 1.0 / 16.0)))) + 8, 0, 255);
    uint3 lost = uint3(round(saturate(h.transmit) * 255.0));

    return uint4(
        uint(h.voxel.x & 0xFFFF) | uint(h.voxel.z & 0xFFFF) << 16,
        uint(h.voxel.y & 0xFFF) | (h.material & 0xFFu) << 12 | (h.kind & 7u) << 20 | flags << 23 | face << 26,
        uint(cellLog) | lost.x << 8 | lost.y << 16 | lost.z << 24,
        linearToSrgbByte(h.color));
}

Hit unpackHit(uint4 v, float t, float3 d)
{
    Hit h = noHit();
    h.voxel = int3(int(v.x & 0xFFFF), int(v.y & 0xFFF), int(v.x >> 16));
    h.material = (v.y >> 12) & 0xFFu;
    h.kind = (v.y >> 20) & 7u;
    uint flags = (v.y >> 23) & 7u;
    h.far = (flags & 1u) != 0u;
    h.micro = (flags & 2u) != 0u;
    h.hit = (flags & 4u) != 0u;
    uint face = (v.y >> 26) & 7u;
    h.normal = face == 0u ? float3(1, 0, 0) : face == 1u ? float3(-1, 0, 0) : face == 2u ? float3(0, 1, 0)
             : face == 3u ? float3(0, -1, 0) : face == 4u ? float3(0, 0, 1) : face == 5u ? float3(0, 0, -1) : -d;
    h.cell = exp2(float(int(v.z & 0xFFu) - 8));
    h.transmit = float3((v.z >> 8) & 0xFFu, (v.z >> 16) & 0xFFu, v.z >> 24) / 255.0;
    h.color = unpackColor(v.w);
    h.t = t;
    return h;
}
