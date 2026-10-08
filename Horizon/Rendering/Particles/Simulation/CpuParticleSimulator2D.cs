using System.Numerics;

using Horizon.Graphics;

namespace Horizon.Rendering.Particles.Simulation;

/// <summary>
/// Simulates particles on the CPU, on the simulation thread.
/// Live particles are always packed in [0, Count), so only live particles are simulated,
/// uploaded and drawn. The instances are written straight into a persistently mapped, triple-buffered
/// buffer the renderer reads, see <see cref="StreamBuffer{T}"/>.
/// </summary>
public sealed class CpuParticleSimulator2D : ParticleSimulator2D
{
    // The instances of the frames, written straight into memory the GPU reads, three frames' worth that take turns
    private StreamBuffer<ParticleInstance>? instances;

    // Simulation state, only ever touched by the simulation thread. Indices are NOT stable
    // (dead particles are removed by swapping the last live particle into their slot)
    private ParticleState2D[] particles = [];
    private int count;

    // The simulation thread fills `back`, then swaps it in as `front` for the render thread to upload,
    // so a frame never sees a half-simulated step
    private readonly Lock frameLock = new();
    private ParticleInstance[] back = [];
    private ParticleInstance[] front = [];
    private int frontCount;

    // How long the particles have been simulated for, and as of when `front` was made and what was last handed out
    private double time, frontTime, preparedTime = double.NaN;

    protected internal override double PreparedTime => preparedTime;

    public override uint Count => (uint)count;

    protected internal override void Initialize()
    {
        particles = new ParticleState2D[Maximum];
        back = new ParticleInstance[Maximum];
        front = new ParticleInstance[Maximum];

        instances = new StreamBuffer<ParticleInstance>(BufferUsage.Storage, (int)Maximum, "particle instances");
    }

    protected internal override void Update(float dt)
    {
        var state = particles;
        var render = back;
        int live = count;

        // Anything that doesn't fit exceeds capacity this frame, so it is dropped
        var spawned = TakePending();
        int room = Math.Min(spawned.Length, state.Length - live);
        spawned[..room].CopyTo(state.AsSpan(live));
        live += room;

        float decay = dt / Renderer.MaxAge;
        Vector2 gravityStep = Renderer.Gravity * dt;

        for (int i = 0; i < live;)
        {
            ref ParticleState2D p = ref state[i];
            p.Life -= decay * p.Rate;

            if (p.Life <= 0.0f)
            {
                // Swap-remove, the last live particle (not yet updated this frame) takes this slot,
                // then index i is done again without advancing
                live--;
                if (i != live)
                    p = state[live];
                continue;
            }

            p.Velocity += gravityStep;
            p.Position += p.Velocity * dt;

            render[i].Offset = p.Position;
            render[i].Alive = p.Life;
            render[i].Velocity = p.Velocity;
            i++;
        }

        count = live;
        time += dt;

        lock (frameLock)
        {
            (front, back) = (back, front);
            frontCount = live;
            frontTime = time;
        }
    }

    protected internal override bool Prepare(out ParticleDraw draw)
    {
        draw = default;
        if (instances is null || frontCount < 1)
            return false;

        // One bulk copy of only the live particles into this frame's region of the stream, which waits for the GPU
        // to be done reading it (three frames on, it is)
        lock (frameLock)
        {
            int live = frontCount;
            Span<ParticleInstance> into = instances.Begin(live);
            if (into.IsEmpty)
                return false;

            front.AsSpan(0, live).CopyTo(into);
            preparedTime = frontTime;
        }

        instances.BindRange(INSTANCES_BINDING);
        draw = new ParticleDraw((uint)frontCount, 0);
        return true;
    }

    protected internal override void Submitted() => instances?.End();

    public override void Dispose()
    {
        instances?.Dispose();
        instances = null;

        base.Dispose();
    }
}
