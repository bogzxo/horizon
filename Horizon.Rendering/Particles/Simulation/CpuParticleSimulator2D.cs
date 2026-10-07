using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Horizon.Engine;
using Horizon.OpenGL.Assets;
using Horizon.OpenGL.Buffers;
using Horizon.OpenGL.Descriptions;

using Silk.NET.OpenGL;

namespace Horizon.Rendering.Particles.Simulation;

/// <summary>
/// Simulates particles on the CPU, on the simulation thread.
/// Live particles are always packed in [0, Count), so only live particles are simulated,
/// uploaded and drawn. GPU data is written through a persistently mapped, triple-buffered
/// instance buffer guarded by fences.
/// </summary>
public sealed class CpuParticleSimulator2D : ParticleSimulator2D
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

    // Simulation state, only ever touched by the simulation thread. Indices are NOT stable
    // (dead particles are removed by swapping the last live particle into their slot).
    private ParticleState2D[] particles = [];
    private int count;

    // The simulation thread fills `back`, then swaps it in as `front` for the render thread to upload,
    // so a frame never sees a half-simulated step.
    private readonly Lock frameLock = new();
    private ParticleRenderData[] back = [];
    private ParticleRenderData[] front = [];
    private int frontCount;

    // How long the particles have been simulated for, and as of when `front` was made and what was last handed out
    private double time, frontTime, preparedTime = double.NaN;

    protected internal override double PreparedTime => preparedTime;

    public override uint Count => (uint)count;

    protected internal override unsafe void Initialize(VertexBufferObject mesh)
    {
        particles = new ParticleState2D[Maximum];
        back = new ParticleRenderData[Maximum];
        front = new ParticleRenderData[Maximum];

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

    protected internal override void Update(float dt)
    {
        var state = particles;
        var render = back;
        int live = count;

        // Anything that doesn't fit exceeds capacity this frame: drop it.
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
                // Swap-remove: the last live particle (not yet updated this frame) takes this slot,
                // then we re-process index i without advancing.
                live--;
                if (i != live)
                    p = state[live];
                continue;
            }

            p.Velocity += gravityStep;
            p.Position += p.Velocity * dt;

            render[i].offset = p.Position;
            render[i].alive = p.Life;
            render[i].velocity = p.Velocity;
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

    protected internal override unsafe ParticleRange Prepare()
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
            preparedTime = frontTime;
            return new ParticleRange(first, (uint)live);
        }
    }

    protected internal override void Submitted()
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
