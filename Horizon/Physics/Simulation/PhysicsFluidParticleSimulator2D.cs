using Horizon.Graphics;
using Horizon.Rendering.Particles.Simulation;

namespace Horizon.Physics.Simulation;

/// <summary>
/// Hands the particles of a renderer over to a <see cref="PhysicsWorld"/>, which simulates them as small dynamic bodies
/// that also collide with one another. They stack up and run off to fill whatever they are poured into
/// (see <see cref="PhysicsFluidParticleGroup"/>). This is the one for water, lava, sand and the like.
/// Unlike <see cref="PhysicsParticleSimulator2D"/> these are stepped by the world on the CPU, which costs a good deal more
/// per particle, so that one is still the better choice for anything that only has to land on the map (sparks, rain, petals).
/// All this class does itself is pass new particles on to the world and copy where they ended up to the GPU, through a
/// persistently mapped, triple-buffered buffer, see <see cref="StreamBuffer{T}"/>.
/// </summary>
public sealed class PhysicsFluidParticleSimulator2D : ParticleSimulator2D
{
    // The instances of the frames, written straight into memory the GPU reads, three frames' worth that take turns
    private StreamBuffer<ParticleInstance>? instances;

    // The simulation thread fills `back`, then swaps it in as `front` for the render thread to upload,
    // so a frame never sees a half-simulated step
    private readonly Lock frameLock = new();
    private readonly PhysicsWorld world;
    private ParticleInstance[] back = [];
    private ParticleInstance[] front = [];
    private int frontCount;

    /// <summary>
    /// The particles as the physics world has them, this is where their radius, friction, mass and spread are set.
    /// The radius is what decides how much room every particle takes up in a pile.
    /// </summary>
    public PhysicsFluidParticleGroup Particles { get; }

    /// <summary>
    /// The longest (in seconds) any particle is kept, 0 for no limit. The renderer's MaxAge is what a particle fades over,
    /// and each one does so at its own rate (the slow ones take up to twice as long), this cuts them all off at the same age.
    /// </summary>
    public float MaxLife
    {
        get => Particles.MaxLife;
        set => Particles.MaxLife = value;
    }

    public override uint Count => (uint)Particles.Count;

    public PhysicsFluidParticleSimulator2D(in PhysicsWorld world)
    {
        this.world = world;

        // How many there can be isn't known until the renderer has us, see Update
        Particles = world.CreateFluidParticleGroup(0);
    }

    protected internal override void Initialize()
    {
        instances = new StreamBuffer<ParticleInstance>(BufferUsage.Storage, (int)Maximum, "particle instances");
    }

    protected internal override void Update(float dt)
    {
        if (back.Length != Maximum)
        {
            back = new ParticleInstance[Maximum];
            lock (frameLock)
            {
                front = new ParticleInstance[Maximum];
                frontCount = 0;
            }
        }

        // The renderer is where the look of the particles is set, the world has to follow along
        Particles.Capacity = (int)Maximum;
        Particles.Gravity = Renderer.Gravity;
        Particles.MaxAge = Renderer.MaxAge;

        // Anything that doesn't fit exceeds capacity this frame, so it is dropped
        Particles.Add(TakePending());

        // The moving is done by the world in its physics step, here we only copy where everything is for drawing.
        // How fast they go is taken as the eye would have it rather than the solver, see PhysicsFluidParticleGroup.Flow
        var state = Particles.Particles;
        var flow = Particles.Flow;
        var render = back;

        for (int i = 0; i < state.Length; i++)
        {
            render[i].Offset = state[i].Position;
            render[i].Alive = state[i].Life;
            render[i].Velocity = flow[i];
        }

        lock (frameLock)
        {
            (front, back) = (back, front);
            frontCount = state.Length;
        }
    }

    protected internal override bool Prepare(out ParticleDraw draw)
    {
        draw = default;
        if (instances is null || frontCount < 1)
            return false;

        // One bulk copy of only the live particles into this frame's region of the stream, which waits for the GPU
        // to be done reading it (three frames on, it is)
        int live;
        lock (frameLock)
        {
            live = frontCount;
            Span<ParticleInstance> into = instances.Begin(live);
            if (into.IsEmpty)
                return false;

            front.AsSpan(0, live).CopyTo(into);
        }

        instances.BindRange(INSTANCES_BINDING);
        // As many as were copied, the same slip the CPU particles had. The world can have swapped again by now
        // and the new count drew a few drops of water nobody had written, out of whatever was left in the buffer
        draw = new ParticleDraw((uint)live, 0);
        return true;
    }

    protected internal override void Submitted() => instances?.End();

    public override void Dispose()
    {
        // The world would otherwise keep simulating particles nobody draws
        world.RemoveParticleGroup(Particles);

        instances?.Dispose();
        instances = null;

        base.Dispose();
    }
}
