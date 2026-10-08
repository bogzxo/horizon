// The particle renderer. The one quad every particle is drawn as, with what is different for every particle coming
// out of a buffer of instances. A simulator on the CPU writes that buffer every frame, the compute simulator has
// the GPU compact its live particles into it (particle_compact.slang) and draws them with an indirect draw, so no
// dead particle ever gets this far.

#include <common/camera.slang>
#include <common/gbuffer.slang>
#include <common/quad.slang>

struct Params
{
    float3 uStartColor;
    float3 uEndColor;

    // How much of a particle shows no matter the light, at the start and at the end of its life
    float uStartEmissive;
    float uEndEmissive;

    // How near the particles are, from 0 (the backdrop) to 1 (right in front)
    float uNearness;

    // Half the side of the quad a particle is drawn as, in world units
    float uParticleSize;

    // How far a particle is drawn out along the way it moves. It is as long as the way it goes in this many seconds,
    // but no longer than the limit (in units of the world) and never shorter than it is anyway
    float uStretch;
    float uMaxStretch;

    // How far (in seconds, back is negative) from where the particles were last moved to the moment the frame shows.
    // Only for particles that are moved a tick at a time, see ParticleRenderer2D. Nothing for those that are moved every frame
    float uTimeOffset;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// Must match ParticleInstance in ParticleSimulator2D.cs (24 bytes, the velocity on 16).
struct ParticleInstance
{
    float2 offset;
    float alive;
    float padding;
    float2 velocity;
};

BIND_BUFFER(3) StructuredBuffer<ParticleInstance> instances;

struct Varyings
{
    float4 position : SV_Position;
    float alive : ALIVE;
    float2 fragPos : FRAGPOS;

    // How fast the particle goes across the screen (halves of it a second), for whoever blurs motion
    nointerpolation float2 motion : MOTION;
};

[shader("vertex")]
Varyings vsMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID, uint baseInstance : SV_StartInstanceLocation)
{
    ParticleInstance particle = instances[quadInstance(instanceId, baseInstance)];
    float2 corner = QUAD_POSITIONS[quadCorner(vertexId)] * (2.0 * params.uParticleSize);

    Varyings output;
    output.alive = particle.alive;
    output.motion = screenMotion(particle.velocity);

    // The corners at the back stay behind where the particle was a moment ago, which draws a drop out into a streak.
    // A stream that falls faster and faster would come apart into a string of dots otherwise
    float speed = length(particle.velocity);
    float tail = min(speed * params.uStretch, params.uMaxStretch) - 2.0 * abs(corner.x);

    if (tail > 0.0)
    {
        float2 heading = particle.velocity / speed;
        if (dot(corner, heading) < 0.0) corner -= heading * tail;
    }

    output.fragPos = particle.offset + particle.velocity * params.uTimeOffset + corner;

    // A dead one that got this far (the CPU simulators hand over whole arrays) is pushed outside the clip volume
    output.position = particle.alive > 0.0
        ? toClip(float4(output.fragPos, 0.0, 1.0))
        : float4(2.0, 2.0, 2.0, 1.0);
    return output;
}

[shader("fragment")]
GBuffer fsMain(Varyings input)
{
    if (input.alive <= 0.0) discard;

    // Particles fade out as they die, and so does what they say about the surface, a spark that is nearly gone
    // must not leave whatever is behind it glowing. A particle isn't shiny, and takes the shine off of what is
    // behind it as far as it covers it
    return writeFlat(
        float4(lerp(params.uEndColor * 2.0, params.uStartColor, input.alive), input.alive),
        lerp(params.uEndEmissive, params.uStartEmissive, input.alive),
        input.motion,
        params.uNearness);
}
