using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

using Shader = Horizon.OpenGL.Assets.Shader;

namespace Horizon.Rendering.Particles.Simulation;

/// <summary>
/// Simulates particles on the GPU with a compute shader (shaders/particle/simulate.comp).
/// The particle state never leaves the GPU: the buffer the compute shader updates in place is the
/// same buffer the renderer reads its instances from, and the CPU only uploads newly spawned particles.
/// A simulator that does more to its particles derives from this one, hands over its own compute shader
/// (see <see cref="CreateShader"/>) and feeds it whatever else it needs in <see cref="BindSimulation"/>.
/// </summary>
public class ComputeParticleSimulator2D : ParticleSimulator2D
{
    /// <summary>Must match local_size_x in simulate.comp.</summary>
    private const uint WorkGroupSize = 256;

    /// <summary>Must match the binding of ParticleBuffer in simulate.comp.</summary>
    private const uint BUFFER_BINDING = 0;

    private const string UNIFORM_FIRST = "uFirst";
    private const string UNIFORM_COUNT = "uCount";
    private const string UNIFORM_DELTA_TIME = "uDeltaTime";
    private const string UNIFORM_INV_MAX_AGE = "uInvMaxAge";
    private const string UNIFORM_GRAVITY = "uGravity";

    /// <summary>
    /// Extra life a batch is given before it is written off, to absorb the rounding error the GPU
    /// accumulates while ageing its particles in single precision.
    /// </summary>
    private const double ExpiryMargin = 0.05;

    private Technique? compute;
    private BufferObject? particleBuffer;

    // The logic thread only banks time, the simulation itself has to run on the GL thread.
    private readonly Lock timeLock = new();
    private float pendingTime;

    // The pool is a ring: particles are written at the head and replace the oldest once it is full, so
    // the live ones always sit in [tail, head). Both are running totals, the slot is the total modulo
    // Maximum.
    private ulong head;
    private ulong tail;
    private uint count;

    // The CPU can't see which particles died, but it knows how fast the slowest one ages. The clock is
    // the life that particle has lost so far; a batch born at clock b is certainly dead by b + 1.
    private double decayClock;
    private readonly Queue<(ulong End, double Born)> batches = new();

    public override uint Count => count;

    /// <summary>
    /// GL thread, once. The compute shader that moves the particles, null if it couldn't be made.
    /// It has to take the buffer and the uniforms simulate.comp does, anything on top of those is up to
    /// <see cref="BindSimulation"/>.
    /// </summary>
    protected virtual Shader? CreateShader() =>
        GameEngine
            .Instance
            .ObjectManager
            .Shaders
            .TryCreateOrGet(
                "particle2d_simulate",
                ShaderDescription.FromPath("shaders/particle", "simulate"),
                out var shader)
            ? shader.Asset
            : null;

    /// <summary>
    /// GL thread, every time the particles are about to be moved, with <paramref name="technique"/> bound.
    /// Bind the buffers and set the uniforms the shader of <see cref="CreateShader"/> has of its own.
    /// </summary>
    protected virtual void BindSimulation(Technique technique)
    { }

    protected internal override void Initialize(VertexBufferObject mesh)
    {
        var objects = GameEngine.Instance.ObjectManager;

        // Failures are logged by the asset managers; without both there is nothing to simulate or draw.
        if (CreateShader() is not Shader shader
            || !objects
                .Buffers
                .TryCreate(
                    new BufferObjectDescription
                    {
                        IsStorageBuffer = true,
                        // Never mapped: spawns go in through glBufferSubData, the rest happens on the GPU.
                        StorageMasks = BufferStorageMask.DynamicStorageBit,
                        // Bound as a storage buffer for the compute shader, as an array buffer for drawing.
                        Type = BufferTargetARB.ArrayBuffer,
                        Size = Maximum * ParticleState2D.SizeInBytes
                    },
                    out var buffer))
        {
            return;
        }

        compute = new Technique(shader);
        particleBuffer = buffer.Asset;
        AttachInstanceBuffer(mesh, particleBuffer, ParticleState2D.SizeInBytes, 0, sizeof(float) * 4);
    }

    protected internal override void Update(float dt)
    {
        lock (timeLock)
            pendingTime += dt;
    }

    protected internal override ParticleRange Prepare()
    {
        if (compute is null || particleBuffer is null)
            return default;

        Upload(particleBuffer, TakePending());

        float dt;
        lock (timeLock)
        {
            dt = pendingTime;
            pendingTime = 0.0f;
        }

        if (dt > 0.0f && head != tail)
        {
            float maxAge = Renderer.MaxAge;

            Simulate(compute, particleBuffer, LiveRange(), dt, maxAge);
            Expire(dt, maxAge);
        }

        var range = LiveRange();
        count = range.Count;
        return range;
    }

    private void Upload(BufferObject buffer, ReadOnlySpan<ParticleState2D> spawned)
    {
        if (spawned.IsEmpty)
            return;

        // At most Maximum particles can be pending, so this wraps around the end of the pool once at most.
        int slot = (int)(head % Maximum);
        int untilWrap = Math.Min(spawned.Length, (int)Maximum - slot);

        Write(buffer, spawned[..untilWrap], slot);
        Write(buffer, spawned[untilWrap..], 0);

        head += (ulong)spawned.Length;
        if (head - tail > Maximum)
            tail = head - Maximum;

        batches.Enqueue((head, decayClock));
    }

    private static void Write(BufferObject buffer, ReadOnlySpan<ParticleState2D> states, int slot)
    {
        if (!states.IsEmpty)
            buffer.NamedBufferSubData(states, slot * (int)ParticleState2D.SizeInBytes);
    }

    private void Simulate(Technique technique, BufferObject buffer, ParticleRange range, float dt, float maxAge)
    {
        var gl = GameEngine.Instance.GL;
        Vector2 gravity = Renderer.Gravity;

        technique.Bind();
        technique.BindBuffer(BUFFER_BINDING, buffer);
        technique.SetUniform(UNIFORM_FIRST, range.First);
        technique.SetUniform(UNIFORM_COUNT, range.Count);
        technique.SetUniform(UNIFORM_DELTA_TIME, dt);
        technique.SetUniform(UNIFORM_INV_MAX_AGE, 1.0f / maxAge);
        technique.SetUniform(UNIFORM_GRAVITY, in gravity);
        BindSimulation(technique);

        gl.DispatchCompute((range.Count + WorkGroupSize - 1) / WorkGroupSize, 1, 1);

        // What the shader wrote is read next as instance attributes, by the next dispatch, and
        // overwritten by the next spawn upload.
        gl.MemoryBarrier(
            MemoryBarrierMask.VertexAttribArrayBarrierBit
                | MemoryBarrierMask.ShaderStorageBarrierBit
                | MemoryBarrierMask.BufferUpdateBarrierBit);

        technique.Unbind();
    }

    private void Expire(float dt, float maxAge)
    {
        decayClock += (double)dt * MinRate / maxAge;

        while (batches.TryPeek(out var batch) && decayClock - batch.Born > 1.0 + ExpiryMargin)
        {
            tail = Math.Max(tail, batch.End);
            batches.Dequeue();
        }
    }

    private ParticleRange LiveRange()
    {
        if (head == tail)
            return default;

        uint live = (uint)(head - tail);
        uint first = (uint)(tail % Maximum);

        // A window that wraps around the end of the pool isn't one run of instances, so cover the whole
        // pool instead. Wrapping means every slot has been written, and the ones outside the window
        // hold dead particles, which the shaders skip.
        return first + live > Maximum
            ? new ParticleRange(0, Maximum)
            : new ParticleRange(first, live);
    }
}
