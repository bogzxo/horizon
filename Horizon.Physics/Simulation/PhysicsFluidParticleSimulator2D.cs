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
/// GPU data is written through a persistently mapped, triple-buffered instance buffer guarded by fences.
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

    /// <summary>Number of regions in the instance buffer (CPU writes one while the GPU reads the others).</summary>
    private const int BufferCount = 3;

    private const SyncObjectMask FlushCommands = (SyncObjectMask)0x1; // GL_SYNC_FLUSH_COMMANDS_BIT

    private BufferObject? instanceBuffer;
    private unsafe ParticleRenderData* renderDataPtr;
    private readonly nint[] fences = new nint[BufferCount];
    private int frameIndex;

    // The logic thread fills `back`, then swaps it in as `front` for the render thread to upload,
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

    protected override unsafe void Initialize(VertexBufferObject mesh)
    {
        uint sizeInBytes = Maximum * BufferCount * ParticleRenderData.SizeInBytes;

        if (!GameEngine
                .Instance
                .ObjectManager
                .Buffers
                .TryCreate(
                    new BufferObjectDescription
                    {
                        IsStorageBuffer = true,
                        // Write-only: reading write-combined memory is very slow and we never do it.
                        StorageMasks =
                            BufferStorageMask.MapCoherentBit
                            | BufferStorageMask.MapPersistentBit
                            | BufferStorageMask.MapWriteBit,
                        Type = BufferTargetARB.ArrayBuffer,
                        Size = sizeInBytes
                    },
                    out var result
                )
        )
        {
            return;
        }

        instanceBuffer = result.Asset;
        AttachInstanceBuffer(mesh, instanceBuffer, ParticleRenderData.SizeInBytes, 0, sizeof(float) * 2, sizeof(float) * 3);

        // Map the whole ring once.
        renderDataPtr = (ParticleRenderData*)
            instanceBuffer.MapBufferRange(
                sizeInBytes,
                MapBufferAccessMask.CoherentBit
                    | MapBufferAccessMask.WriteBit
                    | MapBufferAccessMask.PersistentBit
            );
    }

    protected override void Update(float dt)
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

    protected override unsafe ParticleRange Prepare()
    {
        if (renderDataPtr == null || frontCount < 1)
            return default;

        int slot = frameIndex;
        uint first = (uint)slot * Maximum;

        // Make sure the GPU is done reading this region from BufferCount frames ago.
        WaitForSlot(GameEngine.Instance.GL, slot);

        // One bulk copy of only the live particles into this frame's region
        // (no per-element loop, no full memory barrier needed with a coherent mapping).
        lock (frameLock)
        {
            int live = frontCount;
            front.AsSpan(0, live).CopyTo(new Span<ParticleRenderData>(renderDataPtr + first, live));
            return new ParticleRange(first, (uint)live);
        }
    }

    protected override void Submitted()
    {
        fences[frameIndex] = GameEngine
            .Instance
            .GL
            .FenceSync(SyncCondition.SyncGpuCommandsComplete, SyncBehaviorFlags.None);
        frameIndex = (frameIndex + 1) % BufferCount;
    }

    private void WaitForSlot(GL gl, int slot)
    {
        nint sync = fences[slot];
        if (sync == 0)
            return;

        while ((SyncStatus)gl.ClientWaitSync(sync, FlushCommands, 1_000_000) == SyncStatus.TimeoutExpired)
        {
        }

        gl.DeleteSync(sync);
        fences[slot] = 0;
    }

    public override void Dispose()
    {
        // The world would otherwise keep simulating particles nobody draws
        world.RemoveParticleGroup(Particles);

        var gl = GameEngine.Instance.GL;
        for (int i = 0; i < fences.Length; i++)
        {
            if (fences[i] != 0)
            {
                gl.DeleteSync(fences[i]);
                fences[i] = 0;
            }
        }

        base.Dispose();
    }
}
