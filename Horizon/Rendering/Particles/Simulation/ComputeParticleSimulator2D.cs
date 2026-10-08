using System.Numerics;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Logging;

namespace Horizon.Rendering.Particles.Simulation;

/// <summary>
/// Simulates particles on the GPU with a compute shader (shaders/particle/simulate.slang). The particle state never
/// leaves the GPU. The CPU only uploads newly spawned particles into a pool that goes round, the GPU ages and moves
/// what is in the pool, gathers up what is still alive (shaders/particle/particle_compact.slang) and writes the draw
/// that draws exactly those, so the CPU never has to know which died and the vertex shader never sees one that did.
/// A simulator that does more to its particles derives from this one, hands over its own compute shader (see
/// <see cref="CreateShader"/>) and feeds it whatever else it needs in <see cref="BindSimulation"/>.
/// </summary>
public class ComputeParticleSimulator2D : ParticleSimulator2D
{
    /// <summary>Must match numthreads in simulate.slang.</summary>
    private const uint WorkGroupSize = 256;

    /// <summary>Must match the bindings of pool.slang and particle_compact.slang.</summary>
    private const uint POOL_BINDING = 2;
    private const uint COMPACT_INSTANCES_BINDING = 3;
    private const uint COMMAND_BINDING = 4;

    private const string UNIFORM_FIRST = "uFirst";
    private const string UNIFORM_COUNT = "uCount";
    private const string UNIFORM_CAPACITY = "uCapacity";
    private const string UNIFORM_DELTA_TIME = "uDeltaTime";
    private const string UNIFORM_INV_MAX_AGE = "uInvMaxAge";
    private const string UNIFORM_GRAVITY = "uGravity";

    private static Technique? compactShader;

    private Technique? compute;
    private GpuBuffer? pool, instances, command;

    // The simulation thread only banks time, the simulation itself has to run on the render thread
    private readonly Lock timeLock = new();
    private float pendingTime;

    // The pool is a ring. Particles are written at the head and replace the oldest once it is full. How many slots
    // have ever been written is how many the GPU looks at
    private ulong head;

    public override uint Count => (uint)Math.Min(head, Maximum);

    /// <summary>
    /// Render thread, once. The compute shader that moves the particles, null if it couldn't be made.
    /// It has to take the pool and the params pool.slang does, anything on top of those is up to <see cref="BindSimulation"/>.
    /// </summary>
    protected virtual Shader? CreateShader() =>
        GameEngine.Instance.ObjectManager.Shaders.TryCreateOrGet("particle2d_simulate", ShaderDescription.FromPath("shaders/particle", "simulate"), out var shader)
            ? shader.Asset
            : null;

    /// <summary>
    /// Render thread, every time the particles are about to be moved, with <paramref name="technique"/> bound.
    /// Bind the buffers and set the uniforms the shader of <see cref="CreateShader"/> has of its own.
    /// </summary>
    protected virtual void BindSimulation(Technique technique)
    { }

    protected internal override void Initialize()
    {
        if (CreateShader() is not Shader shader)
            return;

        if (compactShader is null)
        {
            if (!GameEngine.Instance.ObjectManager.Shaders.TryCreateOrGet("particle2d_compact", ShaderDescription.FromPath("shaders/particle", "particle_compact"), out var compact))
            {
                Log.Error(compact.Message);
                return;
            }

            compactShader = new Technique(compact.Asset);
        }

        compute = new Technique(shader);
        pool = GpuBuffer.Create(new BufferDescription(BufferUsage.Storage, BufferAccess.Dynamic, Maximum * ParticleState2D.SizeInBytes));
        instances = GpuBuffer.Create(new BufferDescription(BufferUsage.Storage, BufferAccess.Dynamic, Maximum * ParticleInstance.SizeInBytes));
        command = GpuBuffer.Create(new BufferDescription(BufferUsage.Storage | BufferUsage.Indirect, BufferAccess.Dynamic, 16));

        // A draw of the quad, with the instance count for the GPU to fill in every frame
        command.Update<uint>([6, 0, 0, 0]);
    }

    protected internal override void Update(float dt)
    {
        lock (timeLock)
            pendingTime += dt;
    }

    protected internal override bool Prepare(out ParticleDraw draw)
    {
        draw = default;
        if (compute is null || compactShader is null || pool is null || instances is null || command is null)
            return false;

        Upload(TakePending());

        float dt;
        lock (timeLock)
        {
            dt = pendingTime;
            pendingTime = 0.0f;
        }

        // Drawn alongside the simulation, as far as the frames have moved on in the game, not a tick at a time
        if (FrameStep is { } step)
            dt = step;

        uint count = Count;
        if (count == 0) return false;

        var device = GraphicsDevice.Current;
        float maxAge = Renderer.MaxAge;

        if (dt > 0.0f)
        {
            Simulate(compute, count, dt, maxAge);
            device.Barrier(BarrierTargets.ShaderStorage);
        }

        // The live ones gathered up for drawing, and the draw told how many there were
        device.FillBuffer(command, 0, 4, 4);

        compactShader.Bind();
        SetPoolParams(compactShader, count, dt, maxAge);
        compactShader.BindBuffer(POOL_BINDING, pool);
        compactShader.BindBuffer(COMPACT_INSTANCES_BINDING, instances);
        compactShader.BindBuffer(COMMAND_BINDING, command);
        device.Dispatch((count + WorkGroupSize - 1) / WorkGroupSize);
        device.Barrier(BarrierTargets.ShaderStorage | BarrierTargets.VertexAttributes);
        compactShader.Unbind();

        device.BindStorageBuffer(COMMAND_BINDING, null);
        device.BindStorageBuffer(INSTANCES_BINDING, instances);
        draw = new ParticleDraw(0, 0, command);
        return true;
    }

    private void Upload(ReadOnlySpan<ParticleState2D> spawned)
    {
        if (spawned.IsEmpty || pool is null)
            return;

        // At most Maximum particles can be pending, so this wraps around the end of the pool once at most
        int slot = (int)(head % Maximum);
        int untilWrap = Math.Min(spawned.Length, (int)Maximum - slot);

        if (untilWrap > 0) pool.Update(spawned[..untilWrap], slot * (int)ParticleState2D.SizeInBytes);
        if (untilWrap < spawned.Length) pool.Update(spawned[untilWrap..], 0);

        head += (ulong)spawned.Length;
    }

    private void SetPoolParams(Technique technique, uint count, float dt, float maxAge)
    {
        Vector2 gravity = Renderer.Gravity;
        technique.SetUniform(UNIFORM_FIRST, 0u);
        technique.SetUniform(UNIFORM_COUNT, count);
        technique.SetUniform(UNIFORM_CAPACITY, Maximum);
        technique.SetUniform(UNIFORM_DELTA_TIME, dt);
        technique.SetUniform(UNIFORM_INV_MAX_AGE, 1.0f / maxAge);
        technique.SetUniform(UNIFORM_GRAVITY, in gravity);
    }

    private void Simulate(Technique technique, uint count, float dt, float maxAge)
    {
        technique.Bind();
        SetPoolParams(technique, count, dt, maxAge);
        technique.BindBuffer(POOL_BINDING, pool!);
        BindSimulation(technique);

        GraphicsDevice.Current.Dispatch((count + WorkGroupSize - 1) / WorkGroupSize);
        technique.Unbind();
    }

    public override void Dispose()
    {
        pool?.Dispose();
        instances?.Dispose();
        command?.Dispose();
        pool = instances = command = null;

        base.Dispose();
    }
}
