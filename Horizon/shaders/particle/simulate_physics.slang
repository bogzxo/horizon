// The particles of a PhysicsParticleSimulator2D. simulate.slang, plus running into the map and the dynamic bodies
// of a physics world. What there is to run into is uploaded by the simulator, see PhysicsParticleFeed.

#include <particle/pool.slang>

// Must match PhysicsParticleSimulator2D.MaxImpulses.
#define MAX_IMPULSES 8

// How far a fast particle's step can be split up, so it can't skip through a tile.
#define MAX_SUBSTEPS 8

// Being pushed out of one shape can push into the next (a body pressing into the floor).
#define SOLVER_PASSES 2

// Slower than this into a surface and the particle stays down instead of bouncing.
static const float REST_SPEED = 45.0;

// Must match PhysicsGpuShape (40 bytes).
struct Shape
{
    float2 min; // the box around the shape, for rectangles this is the shape itself
    float2 max;
    float2 velocity; // how fast the body it belongs to is moving, zero for the map
    float radius;
    float circle; // 1 for a circle, 0 for a rectangle, 2 for an outline
    float2 range; // for an outline, the first of its pieces in the segments, and how many it has
};

struct Params
{
    PARTICLE_PARAMS

    float uRadius;
    float uRestitution;
    float uFriction;
    float uLinearDrag;

    // None of the following is physics, it is there for the look of things. All of it is off at 0
    float2 uCruise;       // the speed a particle is brought to while it is in the air (rain falls at one speed, wind and all)
    float uCruiseRate;    // how quickly it is brought there, 0 leaves the air to gravity and drag alone
    float uSplash;        // a hard landing stops a particle dead and sends it off along the surface instead of bouncing, at any speed up to this one
    float uSplashFade;    // how much of the life it has left a landing can cost it (0 to 1), a different share for each

    // How much of the speed of a body a particle takes on when it is shoved out of the way, and the most that can be
    float uBodyPush;
    float uBodyPushLimit;

    float2 uMapOrigin;
    int uMapWidth; // in cells, 0 for no map at all
    int uMapHeight;
    int uMapCellCount;
    float uMapCellSize;

    // A particle outside of this box has fallen off the map and has nothing left to land on
    float2 uLostMin;
    float2 uLostMax;

    // The box around all of the bodies, already grown by the radius of a particle
    int uBodyCount;
    float2 uBodiesMin;
    float2 uBodiesMax;

    // xy the centre, z the radius, w impulse over mass
    int uImpulseCount;
    float4 uImpulses[MAX_IMPULSES];
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

BIND_BUFFER(2) RWStructuredBuffer<Particle> particles;
BIND_BUFFER(3) StructuredBuffer<Shape> mapShapes;

// The map is sorted into a grid. The shapes touching cell i are
// mapCells[uMapCellCount + 1 + mapCells[i]] up to mapCells[uMapCellCount + 1 + mapCells[i + 1]].
BIND_BUFFER(4) StructuredBuffer<int> mapCells;
BIND_BUFFER(5) StructuredBuffer<Shape> bodies;

// The pieces of the outlines some bodies have (the silhouette of a sprite), each from xy to zw in the world.
BIND_BUFFER(6) StructuredBuffer<float4> segments;

// Tests a circle (a particle) against a shape, the way it has to be pushed to get out, and how far.
bool overlaps(Shape shape, float2 position, out float2 normal, out float depth)
{
    const float epsilon = 0.0001;

    normal = float2(0.0, 1.0);
    depth = 0.0;

    // Cheap test first, nearly every shape we get asked about is nowhere near
    if (any(position + params.uRadius < shape.min) || any(position - params.uRadius > shape.max)) return false;

    if (shape.circle > 1.5)
    {
        // An outline. Inside of it is having an odd number of its pieces to one side, and the way out is by the
        // nearest of them whichever side of it we are on
        bool inside = false;
        float2 nearest = position;
        float nearestSquared = 1e30;

        int first = int(shape.range.x), last = first + int(shape.range.y);
        for (int i = first; i < last; i++)
        {
            float2 a = segments[i].xy, b = segments[i].zw;

            if ((a.y > position.y) != (b.y > position.y) &&
                position.x < a.x + (position.y - a.y) / (b.y - a.y) * (b.x - a.x)) inside = !inside;

            float2 along = b - a;
            float lengthSquared = dot(along, along);
            float share = lengthSquared > 0.0 ? clamp(dot(position - a, along) / lengthSquared, 0.0, 1.0) : 0.0;
            float2 closest = a + along * share;

            float2 off = position - closest;
            float found = dot(off, off);
            if (found < nearestSquared)
            {
                nearestSquared = found;
                nearest = closest;
            }
        }

        if (!inside && nearestSquared >= params.uRadius * params.uRadius) return false;

        float gap = sqrt(nearestSquared);
        if (gap > epsilon) normal = inside ? (nearest - position) / gap : (position - nearest) / gap;
        depth = inside ? gap + params.uRadius : params.uRadius - gap;
        return last > first;
    }

    if (shape.circle > 0.5)
    {
        float2 away = position - (shape.min + shape.max) * 0.5;
        float distance = length(away);
        float reach = params.uRadius + shape.radius;

        if (distance >= reach) return false;

        if (distance > epsilon) normal = away / distance;
        depth = reach - distance;
        return true;
    }

    // Find the closest point of the rectangle, we are touching if that is within our radius
    float2 offset = position - clamp(position, shape.min, shape.max);
    float offsetSquared = dot(offset, offset);

    if (offsetSquared > params.uRadius * params.uRadius) return false;

    if (offsetSquared > epsilon)
    {
        float distance = sqrt(offsetSquared);
        normal = offset / distance;
        depth = params.uRadius - distance;
        return true;
    }

    // Our centre is inside the rectangle, leave through whichever side is nearest
    float left = position.x - shape.min.x, right = shape.max.x - position.x;
    float down = position.y - shape.min.y, up = shape.max.y - position.y;
    float nearest = min(min(left, right), min(down, up));

    if (nearest == up) normal = float2(0.0, 1.0);
    else if (nearest == down) normal = float2(0.0, -1.0);
    else if (nearest == left) normal = float2(-1.0, 0.0);
    else normal = float2(1.0, 0.0);

    depth = nearest + params.uRadius;
    return true;
}

bool contains(Shape shape, float2 point)
{
    if (any(point <= shape.min) || any(point >= shape.max)) return false;

    // Only asked of the map, which has no outlines
    if (shape.circle > 1.5) return false;

    float2 away = point - (shape.min + shape.max) * 0.5;
    return shape.circle < 0.5 || dot(away, away) < shape.radius * shape.radius;
}

// What tells one particle from the next where something is left to chance, set before anything is done to it.
static uint seed;

// A number from 0 to 1 that is different every time it is asked for, and for every particle.
float chance()
{
    seed = seed * 747796405u + 2891336453u;
    uint word = ((seed >> ((seed >> 28u) + 4u)) ^ seed) * 277803737u;
    return float((word >> 22u) ^ word) / 4294967295.0;
}

// Whether the particle touched the map this step, which is what being on the ground is.
static bool grounded;

// Gets a particle out of whatever it ran into, and works out the speed it leaves with.
void respond(inout Particle p, float2 normal, float depth, float2 surfaceVelocity, float slide, float restitution)
{
    p.position += normal * depth;

    // Everything from here is as seen from the surface, so hitting a moving body works just like hitting the floor
    float2 velocity = p.velocity - surfaceVelocity;
    float into = dot(velocity, normal);

    // Already on our way out
    if (into >= 0.0) return;

    // What splashes doesn't bounce. It stops dead where it comes down and runs off along the surface, to either side
    // and at any speed up to the most there is. Which way and how fast is made up from what only this particle has.
    // Only a real landing counts, not what its own weight presses it into the ground by while it lies there
    if (params.uSplash > 0.0 && -into > max(REST_SPEED, length(params.uGravity) * params.uDeltaTime * 2.0))
    {
        // Which way, and how much of the speed there is to be had, most get little of it
        float way = chance() < 0.5 ? -1.0 : 1.0;
        float kept = chance();

        p.velocity = float2(-normal.y, normal.x) * (way * kept * kept * params.uSplash) + surfaceVelocity;
        p.life *= 1.0 - params.uSplashFade * chance();
        return;
    }

    // Take away the speed going into the surface, and give some back if it was a hard enough hit to bounce
    float bounce = -into > REST_SPEED ? restitution : 0.0;
    velocity -= normal * (into * (1.0 + bounce));

    // Whatever is left along the surface is slowed by friction
    float2 tangent = velocity - normal * dot(velocity, normal);
    velocity -= tangent * (1.0 - slide);

    p.velocity = velocity + surfaceVelocity;
}

// Finds the cells of the map a box reaches into, false if it is entirely off the map.
bool mapCellsOf(float2 low, float2 high, out int2 first, out int2 last)
{
    int2 size = int2(params.uMapWidth, params.uMapHeight);
    first = int2(0, 0);
    last = int2(0, 0);

    if (params.uMapWidth == 0 ||
        any(high < params.uMapOrigin) ||
        any(low > params.uMapOrigin + float2(size) * params.uMapCellSize)) return false;

    first = clamp(int2((low - params.uMapOrigin) / params.uMapCellSize), int2(0, 0), size - 1);
    last = clamp(int2((high - params.uMapOrigin) / params.uMapCellSize), int2(0, 0), size - 1);
    return true;
}

bool collideWithMap(inout Particle p, float slide)
{
    int2 first, last;
    if (!mapCellsOf(p.position - params.uRadius, p.position + params.uRadius, first, last)) return false;

    bool touched = false;
    for (int y = first.y; y <= last.y; y++)
    {
        for (int x = first.x; x <= last.x; x++)
        {
            int cell = y * params.uMapWidth + x;
            int end = mapCells[cell + 1];

            for (int entry = mapCells[cell]; entry < end; entry++)
            {
                float2 normal;
                float depth;
                if (!overlaps(mapShapes[mapCells[params.uMapCellCount + 1 + entry]], p.position, normal, depth)) continue;

                respond(p, normal, depth, float2(0.0, 0.0), slide, params.uRestitution);
                grounded = true;
                touched = true;
            }
        }
    }

    return touched;
}

bool collideWithBodies(inout Particle p, float slide)
{
    bool touched = false;
    for (int i = 0; i < params.uBodyCount; i++)
    {
        float2 normal;
        float depth;
        if (!overlaps(bodies[i], p.position, normal, depth)) continue;

        // Bodies are a lot faster than anything a particle does by itself, taking on all of their speed has the
        // particles fired off rather than pushed aside
        float2 push = bodies[i].velocity * params.uBodyPush;
        float speed = length(push);
        if (speed > params.uBodyPushLimit) push *= params.uBodyPushLimit / speed;

        // A body is soft compared to the map, nothing bounces off of it
        respond(p, normal, depth, push, slide, 0.0);
        touched = true;
    }

    return touched;
}

// Tests if a point is inside of anything of the map.
bool mapContains(float2 point)
{
    int2 cell, unused;
    if (!mapCellsOf(point, point, cell, unused)) return false;

    int index = cell.y * params.uMapWidth + cell.x;
    int end = mapCells[index + 1];

    for (int entry = mapCells[index]; entry < end; entry++)
    {
        if (contains(mapShapes[mapCells[params.uMapCellCount + 1 + entry]], point)) return true;
    }

    return false;
}

// Must match ComputeParticleSimulator2D.WorkGroupSize.
[shader("compute")]
[numthreads(256, 1, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= params.uCount) return;

    uint index = (params.uFirst + id.x) % params.uCapacity;

    Particle p = particles[index];
    if (p.life <= 0.0) return;

    seed = index * 2654435761u + asuint(p.position.x) + asuint(p.life) * 40503u;
    grounded = false;

    p.life -= params.uDeltaTime * p.rate * params.uInvMaxAge;

    // A particle that gravity has taken past the edge of the map has nothing left to land on
    if (any(p.position < params.uLostMin) || any(p.position > params.uLostMax)) p.life = 0.0;

    if (p.life <= 0.0)
    {
        particles[index] = p;
        return;
    }

    // The pushes that were asked for since the last time, away from their centre, the closer the harder
    for (int i = 0; i < params.uImpulseCount; i++)
    {
        float2 away = p.position - params.uImpulses[i].xy;
        float distance = length(away);
        if (distance >= params.uImpulses[i].z) continue;

        // Anything sat exactly on the centre goes straight up
        float2 direction = distance > 0.0001 ? away / distance : float2(0.0, 1.0);
        p.velocity += direction * (params.uImpulses[i].w * (1.0 - distance / params.uImpulses[i].z));
    }

    p.velocity = (p.velocity + params.uGravity * params.uDeltaTime) * max(0.0, 1.0 - params.uLinearDrag * params.uDeltaTime);

    // Never move further than this without testing for collisions, the fast ones get their step split up
    float safeDistance = max(params.uRadius, 4.0);
    int substeps = clamp(int(ceil(length(p.velocity) * params.uDeltaTime / safeDistance)), 1, MAX_SUBSTEPS);
    float substepTime = params.uDeltaTime / float(substeps);
    float slide = max(0.0, 1.0 - params.uFriction * params.uDeltaTime);

    bool nearBodies = params.uBodyCount > 0;

    for (int step = 0; step < substeps; step++)
    {
        float2 before = p.position;
        p.position += p.velocity * substepTime;

        // Most particles are nowhere near a body, one box around all of them rules those out in a single test
        bool reachable = nearBodies && all(p.position >= params.uBodiesMin) && all(p.position <= params.uBodiesMax);

        bool touchedAnything = false;
        for (int pass = 0; pass < SOLVER_PASSES; pass++)
        {
            bool touched = collideWithMap(p, slide);
            if (reachable && collideWithBodies(p, slide)) touched = true;

            if (!touched) break;
            touchedAnything = true;
        }

        // Being pushed out of one tile can end inside the one next to it, and a body can press a particle into the floor
        // harder than the floor pushes back. The map always wins, rather than end up inside of it the particle stays
        // where it was. Unless it was inside already (spawned there), then it has to be let out
        if (touchedAnything && mapContains(p.position) && !mapContains(before))
        {
            p.position = before;
            p.velocity = float2(0.0, 0.0);
            break;
        }
    }

    // What is in the air is brought to the speed it is meant to go at, what lies on the ground is left to lie
    if (params.uCruiseRate > 0.0 && !grounded)
    {
        p.velocity = lerp(p.velocity, params.uCruise, 1.0 - exp(-params.uCruiseRate * params.uDeltaTime));
    }

    particles[index] = p;
}
