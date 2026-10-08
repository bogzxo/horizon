// Gathers the live particles of a pool into the list the renderer draws from, and writes the draw that draws them.
// The CPU only ever knows how many it spawned, the GPU is the one that knows which are still alive, so it is the
// one that says how many instances the draw has. Run after the simulate pass, with the draw command cleared.

#include <particle/pool.slang>

// Must match ParticleInstance in particle.slang.
struct ParticleInstance
{
    float2 offset;
    float alive;
    float padding;
    float2 velocity;
};

// VkDrawIndirectCommand, the instance count is what this fills in.
struct DrawCommand
{
    uint vertexCount;
    uint instanceCount;
    uint firstVertex;
    uint firstInstance;
};

struct Params
{
    PARTICLE_PARAMS
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;
BIND_BUFFER(2) RWStructuredBuffer<Particle> particles;
BIND_BUFFER(3) RWStructuredBuffer<ParticleInstance> instances;
BIND_BUFFER(4) RWStructuredBuffer<DrawCommand> command;

[shader("compute")]
[numthreads(256, 1, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= params.uCount) return;

    uint index = (params.uFirst + id.x) % params.uCapacity;
    Particle p = particles[index];
    if (p.life <= 0.0) return;

    uint slot;
    InterlockedAdd(command[0].instanceCount, 1u, slot);

    ParticleInstance instance;
    instance.offset = p.position;
    instance.alive = p.life;
    instance.padding = 0.0;
    instance.velocity = p.velocity;
    instances[slot] = instance;
}
