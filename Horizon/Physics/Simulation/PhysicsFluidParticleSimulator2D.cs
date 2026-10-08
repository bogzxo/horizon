using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Engine;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.Particles.Simulation;

using Silk.NET.OpenGL;

namespace Horizon.Physics.Simulation;

/// <summary>
/// Hands the particles of a renderer over to a <see cref="PhysicsWorld"/>, which simulates them as small dynamic bodies
/// that also collide with one another: they stack up and run off to fill whatever they are poured into
/// (see <see cref="PhysicsFluidParticleGroup"/>). This is the one for water, lava, sand and the like.
/// Unlike <see cref="PhysicsParticleSimulator2D"/> these are stepped by the world on the CPU, which costs a good deal more
/// per particle: that one is still the better choice for anything that only has to land on the map (sparks, rain, petals).
/// All this class does itself is pass new particles on to the world and copy where they ended up to the GPU.
/// GPU data is written through a persistently mapped, triple-buffered instance buffer, see <see cref="StreamBuffer{T}"/>.
/// </summary>
public sealed class PhysicsFluidParticleSimulator2D : ParticleSimulator2D
{
    // 20 bytes per particle: offset.xy + alive + velocity.xy. Matches attribute 1 (vec2), 2 (float) and 3 (vec2).
    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleRenderData
    {
        public Vector2 offset;
        public float alive;
        public Vector2 velocity;

        public static readonly uint SizeInBytes = (uint)Unsafe.SizeOf<ParticleRenderData>();
    }

    // The instances of the frames, written straight into memory the GPU reads: three frames' worth that take turns, see StreamBuffer
    private StreamBuffer<ParticleRenderData>? instances;

    // The simulation thread fills `back`, then swaps it in as `front` for the render thread to upload,
    // so a frame never sees a half-simulated step.
    private readonly Lock frameLock = new();
    private readonly PhysicsWorld world;
    private ParticleRenderData[] back = [];
    private ParticleRenderData[] front = [];
    private int frontCount;

    /// <summary>
    /// The particles as the physics world has them, this is where their radius, friction, mass and spread are set.
    /// The radius is what decides how much room every particle takes up in a pile.
    /// </summary>
    public PhysicsFluidParticleGroup Particles { get; }

    /// <summary>
    /// The longest (in seconds) any particle is kept, 0 for no limit. The renderer's MaxAge is what a particle fades over,
    /// and each one does so at its own rate (the slow ones take up to twice as long): this cuts them all off at the same age.
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

    protected internal override unsafe void Initialize(VertexBufferObject mesh)
    {
        instances = new StreamBuffer<ParticleRenderData>(BufferTargetARB.ArrayBuffer, (int)Maximum, "particle instances");
        if (instances.Buffer is null)
            return;

        AttachInstanceBuffer(mesh, instances.Buffer, ParticleRenderData.SizeInBytes, 0, sizeof(float) * 2, sizeof(float) * 3);
    }

    protected internal override void Update(float dt)
    {
        if (back.Length != Maximum)
        {
            back = new ParticleRenderData[Maximum];
            lock (frameLock)
            {
                front = new ParticleRenderData[Maximum];
                frontCount = 0;
            }
        }

        // The renderer is where the look of the particles is set, the world has to follow along
        Particles.Capacity = (int)Maximum;
        Particles.Gravity = Renderer.Gravity;
        Particles.MaxAge = Renderer.MaxAge;

        // Anything that doesn't fit exceeds capacity this frame: drop it.
        Particles.Add(TakePending());

        // The moving is done by the world in its physics step, here we only copy where everything is for drawing
        // How fast they go is taken as the eye would have it rather than the solver, see PhysicsFluidParticleGroup.Flow
        var state = Particles.Particles;
        var flow = Particles.Flow;
        var render = back;

        for (int i = 0; i < state.Length; i++)
        {
            render[i].offset = state[i].Position;
            render[i].alive = state[i].Life;
            render[i].velocity = flow[i];
        }

        lock (frameLock)
        {
            (front, back) = (back, front);
            frontCount = state.Length;
        }
    }

    protected internal override ParticleRange Prepare()
    {
        if (instances is null || frontCount < 1)
            return default;

        // One bulk copy of only the live particles into this frame's region of the stream, which waits for the GPU
        // to be done reading it (three frames on, it is)
        lock (frameLock)
        {
            int live = frontCount;
            Span<ParticleRenderData> into = instances.Begin(live);
            if (into.IsEmpty)
                return default;

            front.AsSpan(0, live).CopyTo(into);
            return new ParticleRange((uint)instances.Offset, (uint)live);
        }
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
