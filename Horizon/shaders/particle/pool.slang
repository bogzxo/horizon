// What every compute shader of the particle simulators shares, the particle as it sits in the pool and the params
// that say which stretch of the pool to work on.

// Must match ParticleState2D (24 bytes).
struct Particle
{
    float2 position;
    float2 velocity;
    float life; // 1 at birth, <= 0 once dead
    float rate; // per-particle ageing multiplier
};

// The pool is a ring. The live ones sit in uCount slots from uFirst on, wrapping at uCapacity.
#define PARTICLE_PARAMS \
    uint uFirst; \
    uint uCount; \
    uint uCapacity; \
    float uDeltaTime; \
    float uInvMaxAge; \
    float2 uGravity;
