using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.Particles.Simulation;

/// <summary>
/// Owns the particles of a <see cref="ParticleRenderer2D"/>: it spawns, moves and kills them, and keeps
/// the per-instance buffer the renderer draws from up to date.
/// The engine updates state on the logic thread and renders on the GL thread, so the work is split the
/// same way: <see cref="Update"/> runs on the logic thread and must not touch GL, everything else that
/// is called by the renderer runs on the GL thread.
/// </summary>
public abstract class ParticleSimulator2D : IDisposable
{
    /// <summary>
    /// The slowest a particle can age (see <see cref="ParticleState2D.Rate"/>), which means no particle
    /// outlives <see cref="ParticleRenderer2D.MaxAge"/> / <see cref="MinRate"/> seconds.
    /// </summary>
    protected const float MinRate = 0.5f;

    // Spawn requests can come from any thread; the simulator collects them with TakePending.
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

    /// <summary>
    /// Feeds the renderer's per-instance attributes (1: offset, 2: alive, 3: velocity) from <paramref name="buffer"/>,
    /// which has to be an array buffer. The velocity is in world units a second: it is what the renderer stretches
    /// a particle along (<see cref="ParticleRenderer2D.Stretch"/>) and what tells a renderer that blurs motion how
    /// fast it is going.
    /// </summary>
    protected static void AttachInstanceBuffer(
        VertexBufferObject mesh, BufferObject buffer, uint stride, int offsetOffset, int aliveOffset, int velocityOffset)
    {
        mesh.Bind();
        buffer.Bind();
        buffer.VertexAttributePointer(1, 2, VertexAttribPointerType.Float, stride, offsetOffset);
        buffer.VertexAttributePointer(2, 1, VertexAttribPointerType.Float, stride, aliveOffset);
        buffer.VertexAttributePointer(3, 2, VertexAttribPointerType.Float, stride, velocityOffset);
        buffer.VertexAttributeDivisor(1, 1);
        buffer.VertexAttributeDivisor(2, 1);
        buffer.VertexAttributeDivisor(3, 1);
        mesh.Unbind();
    }

    /// <summary>
    /// GL thread, once. Create the instance buffer and attach it to <paramref name="mesh"/> with
    /// <see cref="AttachInstanceBuffer"/>.
    /// </summary>
    protected internal abstract void Initialize(VertexBufferObject mesh);

    /// <summary>Logic thread, every state update.</summary>
    protected internal abstract void Update(float dt);

    /// <summary>
    /// GL thread, every frame before drawing. Bring the instance buffer up to date and say which
    /// instances are worth drawing.
    /// </summary>
    protected internal abstract ParticleRange Prepare();

    /// <summary>GL thread, after the range returned by <see cref="Prepare"/> has been drawn.</summary>
    protected internal virtual void Submitted()
    { }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
