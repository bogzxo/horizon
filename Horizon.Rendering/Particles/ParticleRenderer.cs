using System.Numerics;

using Horizon.Engine;
using Horizon.OpenGL;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;
using Horizon.Rendering.Particles.Simulation;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.Particles;

/// <summary>
/// A batched and instanced 2D particle systems renderer.
/// The particles themselves are owned by a <see cref="ParticleSimulator2D"/>, which updates them either
/// on the CPU (<see cref="CpuParticleSimulator2D"/>) or on the GPU (<see cref="ComputeParticleSimulator2D"/>)
/// and provides the instance buffer this renderer draws from.
/// </summary>
public class ParticleRenderer2D : GameObject, IDisposable
{
    /// <summary>How many particles the group helpers build on the stack before handing them over.</summary>
    private const int SpawnChunkSize = 256;

    private const string UNIFORM_CAMERA_PROJ_MATRIX = "uCameraProjection";
    private const string UNIFORM_CAMERA_VIEW_MATRIX = "uCameraView";
    private const string UNIFORM_CAMERA_VELOCITY = "uCameraVelocity";
    private const string UNIFORM_MOTION_SCALE = "uMotionScale";
    private const string UNIFORM_NEARNESS = "uNearness";
    private const string UNIFORM_STRETCH = "uStretch";
    private const string UNIFORM_MAX_STRETCH = "uMaxStretch";

    private VertexBufferObject buffer;
    private readonly uint[] indices = { 0, 1, 2, 0, 2, 3 };

    public Technique Material { get; set; }

    /// <summary>The system that spawns, moves and kills this renderer's particles.</summary>
    public ParticleSimulator2D Simulator { get; }

    /// <summary>
    /// The total count of all active particles. An upper bound when simulating on the GPU, see
    /// <see cref="ParticleSimulator2D.Count"/>.
    /// </summary>
    public uint Count => Simulator.Count;

    /// <summary>The maximum age a particle can reach before it is considered dead.</summary>
    public float MaxAge { get; set; } = 2.5f;

    /// <summary>Half the side length of the particle quad (read when <see cref="Initialize"/> runs).</summary>
    public float ParticleSize { get; set; } = 1.0f;

    /// <summary>The maximum number of particles that can exist.</summary>
    public uint Maximum { get; }

    /// <summary>A particles initial color.</summary>
    public Vector3 StartColor { get; set; } = Vector3.One;

    /// <summary>A particles color at the end of its life.</summary>
    public Vector3 EndColor { get; set; } = Vector3.One;

    /// <summary>
    /// How much of a new particle shows no matter the light (0 to 1), for when it is drawn by a <see cref="DeferredRenderer2D"/>:
    /// sparks, flames and anything else that glows is seen in the dark, dust and water are not.
    /// </summary>
    public float StartEmissive { get; set; } = 0.0f;

    /// <summary>How much of a particle shows no matter the light at the end of its life, what glowed can cool off.</summary>
    public float EndEmissive { get; set; } = 0.0f;

    /// <summary>
    /// How much of a particle shows no matter the light, all of its life. Sets both <see cref="StartEmissive"/>
    /// and <see cref="EndEmissive"/>.
    /// </summary>
    public float Emissive
    {
        set => StartEmissive = EndEmissive = value;
    }

    /// <summary>
    /// Acceleration applied to every particle, in world units per second squared.
    /// World space is Y-up, so falling is a negative Y.
    /// </summary>
    public Vector2 Gravity { get; set; } = Vector2.Zero;

    /// <summary>
    /// How far every particle is drawn out along the way it moves, in seconds: it is as long as the way it goes in
    /// that time, whenever that is longer than it is anyway. One that lies still or only creeps along stays the
    /// square it is. 0 for none.
    /// <para>
    /// This is what keeps a stream in one piece. Particles let go one after the other get further apart the faster
    /// they fall, so what leaves as a jet lands as a string of dots, and letting more of them go only crowds
    /// wherever they come out. Drawn as long as the time between two of them (1 / the rate they are let go at),
    /// every one reaches back to the next however fast they have got.
    /// </para>
    /// </summary>
    public float Stretch { get; set; } = 0.0f;

    /// <summary>The longest <see cref="Stretch"/> draws a particle out to, in world units.</summary>
    public float MaxStretch { get; set; } = 32.0f;

    /// <summary>
    /// How near the particles are, from 0 (the backdrop) to 1 (right in front). Only a renderer that blurs motion
    /// goes by it (see <see cref="DeferredRenderer2D"/>): what is nearer blurs over what is further away.
    /// </summary>
    public float Nearness { get; set; } = 0.7f;

    /// <summary>Creates a renderer whose particles are simulated on the CPU.</summary>
    public ParticleRenderer2D(int count)
        : this(count, new CpuParticleSimulator2D()) { }

    public ParticleRenderer2D(int count, ParticleSimulator2D simulator)
    {
        Maximum = (uint)count;
        Material = new Materials.BasicParticle2DTechnique(this);

        Simulator = simulator;
        Simulator.Attach(this);
    }

    public override void Initialize()
    {
        buffer = VertexBufferObject.Create();

        var quadVerts = new ParticleVertex[]
        {
            new(new Vector2(-ParticleSize, -ParticleSize)),
            new(new Vector2(ParticleSize, -ParticleSize)),
            new(new Vector2(ParticleSize, ParticleSize)),
            new(new Vector2(-ParticleSize, ParticleSize))
        };

        // Configure VAO layout
        buffer.Bind();
        buffer.ElementBuffer.Bind(); // attach the EBO to the VAO once; never unbind it per-frame
        buffer.VertexBuffer.Bind();
        buffer.VertexBuffer.VertexAttributePointer(
            0, 2, VertexAttribPointerType.Float, ParticleVertex.SizeInBytes, 0);
        buffer.Unbind();

        buffer.VertexBuffer.NamedBufferData(quadVerts);
        buffer.ElementBuffer.NamedBufferData(indices);

        // Per-instance data (attributes 1 and 2) comes from the simulator.
        Simulator.Initialize(buffer);

        base.Initialize();
    }

    /// <summary>
    /// Queues a particle to be spawned. Thread-safe. Dropped if the system is full.
    /// </summary>
    public void Add(Particle2D input)
    {
        if (!Enabled)
            return;
        Simulator.Spawn(new ReadOnlySpan<Particle2D>(in input));
    }

    /// <summary>
    /// Queues a group of particles to be spawned together. Thread-safe. Whatever doesn't fit is dropped.
    /// </summary>
    public void AddRange(ReadOnlySpan<Particle2D> particles)
    {
        if (!Enabled)
            return;
        Simulator.Spawn(particles);
    }

    /// <summary>
    /// Queues <paramref name="count"/> particles flying out of <paramref name="position"/> in every
    /// direction, e.g. an explosion. Thread-safe.
    /// </summary>
    public void AddBurst(Vector2 position, int count, float speed = 32)
        => AddCone(position, Vector2.UnitX, MathF.Tau, count, speed);

    /// <summary>
    /// Queues <paramref name="count"/> particles sprayed out of <paramref name="position"/> within
    /// <paramref name="spread"/> radians centred on <paramref name="direction"/>, e.g. sparks off a hit,
    /// a jet or a fountain. Thread-safe.
    /// </summary>
    public void AddCone(Vector2 position, Vector2 direction, float spread, int count, float speed = 32)
    {
        if (!Enabled)
            return;

        float centre = MathF.Atan2(direction.Y, direction.X);
        Span<Particle2D> chunk = stackalloc Particle2D[SpawnChunkSize];

        while (count > 0)
        {
            int size = Math.Min(count, chunk.Length);
            for (int i = 0; i < size; i++)
            {
                float angle = centre + (Random.Shared.NextSingle() - 0.5f) * spread;
                var (sin, cos) = MathF.SinCos(angle);
                chunk[i] = new Particle2D(new Vector2(cos, sin), position, speed);
            }

            Simulator.Spawn(chunk[..size]);
            count -= size;
        }
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        if (!Enabled)
            return;

        Simulator.Update(dt);
    }

    public override unsafe void Render(float dt)
    {
        base.Render(dt);

        // The simulator uploads (CPU) or steps (GPU) the particles and says which instances to draw.
        var range = Simulator.Prepare();
        if (range.Count < 1)
            return;

        Material.Bind();
        Material.SetUniform(UNIFORM_CAMERA_PROJ_MATRIX, Engine.ActiveCamera.Projection);
        Material.SetUniform(UNIFORM_CAMERA_VIEW_MATRIX, Engine.ActiveCamera.View);

        // How fast each of them flies comes with the particles. A renderer that blurs motion wants that as seen
        // on the screen: less what the camera does, in halves of the screen
        Vector2 cameraVelocity = Engine.ActiveCamera.Velocity;
        Vector2 motionScale = new(Engine.ActiveCamera.Projection.M11, Engine.ActiveCamera.Projection.M22);
        Material.SetUniform(UNIFORM_CAMERA_VELOCITY, in cameraVelocity);
        Material.SetUniform(UNIFORM_MOTION_SCALE, in motionScale);
        Material.SetUniform(UNIFORM_NEARNESS, Nearness);

        Material.SetUniform(UNIFORM_STRETCH, MathF.Max(Stretch, 0.0f));
        Material.SetUniform(UNIFORM_MAX_STRETCH, MathF.Max(MaxStretch, 0.0f));

        buffer.Bind();

        // baseInstance offsets the divisor-1 attributes to the simulator's range of the instance buffer.
        Engine.GL.DrawElementsInstancedBaseInstance(
            PrimitiveType.Triangles,
            6,
            DrawElementsType.UnsignedInt,
            null,
            range.Count,
            range.First
        );

        buffer.Unbind();
        Material.Unbind();

        Simulator.Submitted();
    }

    protected override void DisposeOther()
    {
        Simulator.Dispose();
        base.DisposeOther();
    }
}
