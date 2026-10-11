// The compute particle simulator. Every particle ages, falls and moves, in place, in the pool the CPU spawns into.
// What is alive afterwards is gathered up for drawing by particle_compact.slang.

#include <particle/pool.slang>

struct Params
{
    PARTICLE_PARAMS
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_BUFFER(2) RWStructuredBuffer<Particle> particles;

// Must match ComputeParticleSimulator2D.WorkGroupSize.
[shader("compute")]
[numthreads(256, 1, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= params.uCount) return;

    uint index = (params.uFirst + id.x) % params.uCapacity;

    Particle p = particles[index];
    if (p.life <= 0.0) return;

    p.life -= params.uDeltaTime * p.rate * params.uInvMaxAge;
    p.velocity += params.uGravity * params.uDeltaTime;
    p.position += p.velocity * params.uDeltaTime;

    particles[index] = p;
}
