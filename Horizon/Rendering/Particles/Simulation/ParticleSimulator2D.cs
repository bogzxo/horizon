using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.Graphics;

namespace Horizon.Rendering.Particles.Simulation;

/// <summary>
/// A particle the way the renderer draws it. Must match ParticleInstance in shaders/particle/particle.slang (24 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ParticleInstance
{
    public Vector2 Offset;
    public float Alive;
    public float Padding;
    public Vector2 Velocity;

    public static readonly uint SizeInBytes = 24;
}

/// <summary>How a simulator's particles are to be drawn this frame, see <see cref="ParticleSimulator2D.Prepare"/>.</summary>
/// <param name="Count">How many instances, for a simulator that knows.</param>
/// <param name="First">The first instance of the bound buffer to draw.</param>
/// <param name="Indirect">A buffer holding the draw itself (a VkDrawIndirectCommand), for a simulator whose GPU knows and the CPU doesn't.</param>
public readonly record struct ParticleDraw(uint Count, uint First, GpuBuffer? Indirect = null)
{
    public bool IsEmpty => Indirect is null && Count == 0;
}

/// <summary>
/// Owns the particles of a <see cref="ParticleRenderer2D"/>. It spawns, moves and kills them, and keeps the buffer
/// of instances the renderer draws from up to date.
/// The engine updates state on the simulation thread and renders on the render thread, so the work is split the
/// same way. <see cref="Update"/> runs on the simulation thread and must not touch the GPU, everything else that
/// is called by the renderer runs on the render thread.
/// </summary>
public abstract class ParticleSimulator2D : IDisposable
{
    /// <summary>The binding the renderer reads the instances at, which is what particle.slang says.</summary>
    public const uint INSTANCES_BINDING = 3;

    /// <summary>
    /// The slowest a particle can age (see <see cref="ParticleState2D.Rate"/>), which means no particle
    /// outlives <see cref="ParticleRenderer2D.MaxAge"/> / <see cref="MinRate"/> seconds.
    /// </summary>
    protected const float MinRate = 0.5f;

    // Spawn requests can come from any thread, the simulator collects them with TakePending
    private readonly Lock spawnLock = new();
    private ParticleState2D[] pending = new ParticleState2D[64];
    private ParticleState2D[] taken = [];
    private int pendingCount;

    /// <summary>The renderer this simulator belongs to, the source of MaxAge, Gravity and Maximum.</summary>
    protected ParticleRenderer2D Renderer { get; private set; } = null!;

    /// <summary>The maximum number of particles that can exist.</summary>
    protected uint Maximum => Renderer.Maximum;

    /// <summary>
    /// The number of particles being simulated and drawn. Simulators that cannot count their live
    /// particles cheaply (the GPU) report an upper bound.
    /// </summary>
    public abstract uint Count { get; }

    internal void Attach(ParticleRenderer2D renderer)
    {
        if (Renderer is not null)
            throw new InvalidOperationException("A particle simulator can only drive one ParticleRenderer2D.");

        Renderer = renderer;
    }

    /// <summary>
    /// Queues particles to be spawned. Thread-safe. Anything beyond <see cref="Maximum"/> queued
    /// particles is dropped.
    /// </summary>
    internal void Spawn(ReadOnlySpan<Particle2D> particles)
    {
        lock (spawnLock)
        {
            int count = Math.Min(particles.Length, (int)Maximum - pendingCount);
            if (count < 1)
                return;

            if (pendingCount + count > pending.Length)
                Array.Resize(
                    ref pending,
                    Math.Min((int)Maximum, Math.Max(pending.Length * 2, pendingCount + count)));

            var states = pending.AsSpan(pendingCount, count);
            for (int i = 0; i < count; i++)
            {
                ref readonly Particle2D particle = ref particles[i];
                float rate = MinRate + Random.Shared.NextSingle() * (1.0f - MinRate);

                states[i] = new ParticleState2D
                {
                    Position = particle.InitialPosition,
                    Velocity = particle.Direction * (particle.Steady ? particle.Speed : particle.Speed * rate),
                    Life = 1.0f,
                    Rate = rate
                };
            }

            pendingCount += count;
        }
    }

    /// <summary>
    /// Hands over every particle queued since the last call, oldest first. The span stays valid until
    /// the next call, so only one thread may be taking.
    /// </summary>
    protected ReadOnlySpan<ParticleState2D> TakePending()
    {
        lock (spawnLock)
        {
            (pending, taken) = (taken, pending);

            int count = pendingCount;
            pendingCount = 0;
            return taken.AsSpan(0, count);
        }
    }

    /// <summary>Render thread, once. Make whatever lives on the GPU.</summary>
    protected internal abstract void Initialize();

    /// <summary>Simulation thread, every state update.</summary>
    protected internal abstract void Update(float dt);

    /// <summary>
    /// Render thread, every frame before drawing. Bring the instances up to date, bind them at
    /// <see cref="INSTANCES_BINDING"/> and say how they are to be drawn. False for nothing to draw.
    /// </summary>
    protected internal abstract bool Prepare(out ParticleDraw draw);

    /// <summary>
    /// For a simulator that moves the particles on the render thread, how far (in seconds of the game) to move them for
    /// this frame instead of by the time the updates banked, null for that. Set by the renderer before
    /// <see cref="Prepare"/> when the frame is drawn alongside the simulation, where the frames show the game between
    /// ticks and the particles have to keep up with that rather than jump a tick at a time.
    /// </summary>
    protected internal float? FrameStep { get; internal set; }

    /// <summary>
    /// For a simulator that moves the particles in the updates, how long (in seconds of the game, counted the way the
    /// renderer counts its updates) it had been simulating for when it made what <see cref="Prepare"/> handed out.
    /// NaN for one that moves them on the render thread. The renderer draws them back along their way by however much
    /// earlier than that the frame shows the game.
    /// </summary>
    protected internal virtual double PreparedTime => double.NaN;

    /// <summary>Render thread, after what <see cref="Prepare"/> handed out has been drawn.</summary>
    protected internal virtual void Submitted()
    { }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
