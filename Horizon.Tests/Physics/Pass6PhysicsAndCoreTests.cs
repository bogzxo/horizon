using System;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;

using Horizon.Audio;
using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Core.Threading;
using Horizon.Core.Tweening;
using Horizon.Physics;
using Horizon.Physics.Fixtures;
using Horizon.Physics.Simulation;
using Horizon.Rendering.Particles.Simulation;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;

using Xunit;

namespace Horizon.Tests;

public class Pass6PhysicsAndCoreTests
{
    private const float STEP_120 = 1.0f / 120.0f;

    [Fact]
    public void Fluid_dense_grid_and_5_cell_stencil_are_deterministic_and_bounded()
    {
        static (Vector2[] Positions, int Count, bool UsedDense, int Pairs) RunSimulation()
        {
            var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -1200.0f) };
            var ground = world.CreateBody(PhysicsBodySimulationType.Static);
            ground.CreateRectangularFixture(new Vector2(-400.0f, -350.0f), new Vector2(800.0f, 50.0f));
            ground.CreateRectangularFixture(new Vector2(0.0f, -300.0f), new Vector2(10.0f, 160.0f));
            ground.CreateRectangularFixture(new Vector2(180.0f, -300.0f), new Vector2(10.0f, 160.0f));

            PhysicsFluidParticleGroup fluid = world.CreateFluidParticleGroup(512);
            fluid.Radius = 3.0f;
            fluid.Friction = 1.0f;
            fluid.MaxLife = 0.0f;
            fluid.MaxAge = 1000.0f;
            fluid.Gravity = new Vector2(0.0f, -1200.0f);

            Span<ParticleState2D> batch = stackalloc ParticleState2D[120];
            for (int i = 0; i < batch.Length; i++)
            {
                batch[i] = new ParticleState2D
                {
                    Position = new Vector2(30.0f + (i % 15) * 8.0f, -260.0f + (i / 15) * 7.0f),
                    Velocity = Vector2.Zero,
                    Life = 1.0f,
                    Rate = 1.0f
                };
            }
            fluid.Add(batch);

            for (int t = 0; t < 180; t++)
                world.UpdatePhysics(STEP_120);

            var positions = new Vector2[fluid.Count];
            for (int i = 0; i < fluid.Count; i++)
                positions[i] = fluid.Particles[i].Position;

            return (positions, fluid.Count, fluid.LastStepUsedDenseGrid, fluid.LastPairCount);
        }

        var runA = RunSimulation();
        var runB = RunSimulation();

        Assert.True(runA.UsedDense, "Expected bounded fluid basin to use the flat 2D dense grid");
        Assert.Equal(120, runA.Count);
        Assert.True(runA.Pairs > 0, "Expected 5-cell half-stencil to find neighbour pairs");
        Assert.Equal(runA.Count, runB.Count);
        Assert.Equal(runA.Pairs, runB.Pairs);

        for (int i = 0; i < runA.Count; i++)
        {
            Assert.Equal(runA.Positions[i], runB.Positions[i]);
            Assert.InRange(runA.Positions[i].X, 5.0f, 185.0f);
            Assert.True(runA.Positions[i].Y > -302.0f, $"Particle {i} fell below floor: {runA.Positions[i].Y}");
        }
    }

    [Fact]
    public void Fluid_unbounded_spread_falls_back_to_spatial_hash_without_dropping_pairs()
    {
        var world = new PhysicsWorld { Gravity = Vector2.Zero };
        PhysicsFluidParticleGroup fluid = world.CreateFluidParticleGroup(16);
        fluid.Radius = 3.0f;
        fluid.MaxAge = 1000.0f;
        fluid.Gravity = Vector2.Zero;

        // Place two colliding particles near the origin and one far away so gridW * gridH > 65536
        Span<ParticleState2D> batch =
        [
            new ParticleState2D { Position = new Vector2(0.0f, 0.0f), Life = 1.0f, Rate = 1.0f },
            new ParticleState2D { Position = new Vector2(2.0f, 0.0f), Life = 1.0f, Rate = 1.0f },
            new ParticleState2D { Position = new Vector2(50000.0f, 50000.0f), Life = 1.0f, Rate = 1.0f }
        ];
        fluid.Add(batch);

        world.UpdatePhysics(STEP_120);

        Assert.False(fluid.LastStepUsedDenseGrid, "Expected huge particle span to fall back to hash grid");
        Assert.True(fluid.LastPairCount >= 1, "Expected hash fallback to still detect the nearby pair");
        float separation = Vector2.Distance(fluid.Particles[0].Position, fluid.Particles[1].Position);
        Assert.True(separation > 2.0f, $"Expected overlapping pair to separate under hash fallback, got {separation}");
    }

    [Fact]
    public void StaticGrid_near_mask_rejects_interior_and_preserves_boundary_and_large_reach_queries()
    {
        var grid = new PhysicsStaticGrid();
        var body = new PhysicsBodyComponent2D { SimulationType = PhysicsBodySimulationType.Static, Position = Vector2.Zero };
        // Two walls far apart so the middle cells are empty and more than 1 cell away from both walls
        body.CreateRectangularFixture(new Vector2(0.0f, 0.0f), new Vector2(16.0f, 16.0f));
        body.CreateRectangularFixture(new Vector2(320.0f, 320.0f), new Vector2(16.0f, 16.0f));

        grid.Refresh([body]);

        Assert.False(grid.IsEmpty);
        Assert.True(grid.NearMask.Length == grid.Width * grid.Height);

        // Interior empty cell (around 160, 160) has mask == 0 and rejects for reach <= CELL_SIZE
        int midCell = (grid.Height / 2) * grid.Width + (grid.Width / 2);
        Assert.Equal(0, grid.NearMask[midCell]);
        Assert.False(grid.IsNearAnyShape(new Vector2(160.0f, 160.0f), PhysicsStaticGrid.CELL_SIZE));

        // Exact boundary just outside origin within CELL_SIZE must NOT be skipped
        Assert.True(grid.IsNearAnyShape(new Vector2(-PhysicsStaticGrid.CELL_SIZE, 0.0f), PhysicsStaticGrid.CELL_SIZE));
        Assert.False(grid.IsNearAnyShape(new Vector2(-PhysicsStaticGrid.CELL_SIZE - 1.0f, 0.0f), PhysicsStaticGrid.CELL_SIZE));

        // Adjacent to first wall (inside dilated mask)
        Assert.True(grid.IsNearAnyShape(new Vector2(20.0f, 8.0f), 8.0f));

        // Large reach > CELL_SIZE must bypass the 1-cell mask and still detect the wall
        Assert.True(grid.IsNearAnyShape(new Vector2(160.0f, 160.0f), 200.0f));
    }

    [Fact]
    public void SpriteTweens_and_UITweens_allocate_zero_bytes_after_warmup_and_animate_correctly()
    {
        var sprite = new Sprite(new Vector2(100.0f, 50.0f));
        var ui = new TestUIComponent();

        // Warm up slot arrays and static delegates once
        sprite.Punch(0.2f, 0.2f);
        sprite.TweenPosition(new Vector2(10.0f, 20.0f), 0.2f);
        sprite.Flash(Vector4.One, 0.2f);
        sprite.Shake(5.0f, 0.2f);
        sprite.Tweens.Tick(0.25f);

        ui.Punch(0.15f, 0.2f);
        ui.TweenOffset(new Vector2(5.0f, 5.0f), 0.2f);
        ui.TweenOpacity(0.5f, 0.2f);
        ui.Shake(4.0f, 0.2f);
        ui.Tweens.Tick(0.25f);

        // Steady-state calls must allocate 0 bytes
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
        {
            sprite.Punch(0.2f, 0.2f);
            sprite.TweenPosition(new Vector2(10.0f, 20.0f), 0.2f);
            sprite.Flash(Vector4.One, 0.2f);
            sprite.Shake(5.0f, 0.2f);
            sprite.Tweens.Tick(0.05f);

            ui.Punch(0.15f, 0.2f);
            ui.TweenOffset(new Vector2(5.0f, 5.0f), 0.2f);
            ui.TweenOpacity(0.5f, 0.2f);
            ui.Shake(4.0f, 0.2f);
            ui.Tweens.Tick(0.05f);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);

        // Verify two-step Punch grows then settles back to original size/scale
        sprite.Tweens.KillAll();
        sprite.Transform.Size = new Vector2(100.0f, 50.0f);
        TweenHandle punch = sprite.Punch(0.2f, 1.0f);
        sprite.Tweens.Tick(0.3f);
        Assert.True(sprite.Transform.Size.X > 115.0f, $"Expected punch peak > 115, got {sprite.Transform.Size.X}");
        sprite.Tweens.Tick(0.75f);
        Assert.True(punch.IsCompleted);
        Assert.Equal(100.0f, sprite.Transform.Size.X, 2);
        Assert.Equal(50.0f, sprite.Transform.Size.Y, 2);

        ui.Tweens.KillAll();
        ui.VisualScale = Vector2.One;
        TweenHandle uiPunch = ui.Punch(0.2f, 1.0f);
        ui.Tweens.Tick(0.3f);
        Assert.True(ui.VisualScale.X > 1.15f);
        ui.Tweens.Tick(0.75f);
        Assert.True(uiPunch.IsCompleted);
        Assert.Equal(1.0f, ui.VisualScale.X, 2);
    }

    [Fact]
    public void Entity_capabilities_and_subtree_flags_skip_empty_physics_walks_and_propagate()
    {
        var root = new TestEntity();
        var branch = root.AddEntity(new TestEntity());
        var leaf = branch.AddEntity(new TestEntity());

        var transform = leaf.AddComponent(new TransformComponent2D());
        var stateOnly = leaf.AddComponent(new CountingComponent(ComponentCapabilities.UpdateState));
        root.InitializeAll();

        Assert.Equal(ComponentCapabilities.None, transform.Capabilities);
        Assert.False(leaf.HasSubtreePhysics);
        Assert.False(branch.HasSubtreePhysics);
        Assert.False(root.HasSubtreePhysics);
        Assert.Equal(0, leaf.PhysicsComponentCount);
        Assert.Equal(1, leaf.StateComponentCount);

        // UpdatePhysics skips the entire non-physics hierarchy; UpdateState still runs stateOnly
        root.UpdatePhysics(STEP_120);
        root.UpdateState(STEP_120);
        Assert.Equal(0, stateOnly.PhysicsCalls);
        Assert.Equal(1, stateOnly.StateCalls);

        // Adding a physics component propagates HasSubtreePhysics up to root
        var physComp = leaf.AddComponent(new CountingComponent(ComponentCapabilities.UpdatePhysics));
        leaf.InitializeAll();

        Assert.True(leaf.HasSubtreePhysics);
        Assert.True(branch.HasSubtreePhysics);
        Assert.True(root.HasSubtreePhysics);

        root.UpdatePhysics(STEP_120);
        Assert.Equal(1, physComp.PhysicsCalls);
        Assert.Equal(0, stateOnly.PhysicsCalls);

        // Removing the physics component clears HasSubtreePhysics back up to root
        Assert.True(leaf.RemoveComponent(physComp));
        Assert.False(leaf.HasSubtreePhysics);
        Assert.False(branch.HasSubtreePhysics);
        Assert.False(root.HasSubtreePhysics);

        // Entity subclass overriding UpdatePhysics is detected and always called
        var customPhysicsChild = branch.AddEntity(new PhysicsOverrideEntity());
        branch.InitializeAll();
        Assert.True(root.HasSubtreePhysics);
        root.UpdatePhysics(STEP_120);
        Assert.Equal(1, customPhysicsChild.PhysicsCalls);
    }

    [Fact]
    public void SnapshotClock_incremental_peak_tracks_and_expires_lateness_spikes()
    {
        long tick = Stopwatch.Frequency / 120;
        var clock = new SnapshotClock();
        long lowLate = tick / 20;
        long spikeLate = tick / 2;

        // Prime steady low lateness
        for (int i = 1; i <= 20; i++)
        {
            clock.BeginCapture();
            clock.EndCapture(i * tick, i / 120.0, i * tick + lowLate);
        }
        double baselineDelay = clock.Delay;

        // Inject a single spike
        clock.BeginCapture();
        clock.EndCapture(21 * tick, 21 / 120.0, 21 * tick + spikeLate);
        double spikedDelay = clock.Delay;
        Assert.True(spikedDelay > baselineDelay + 0.002, $"Expected delay to jump on spike: baseline={baselineDelay}, spiked={spikedDelay}");

        // While the spike is still in the 128-tick window, delay stays at the spiked peak
        for (int i = 22; i <= 140; i++)
        {
            clock.BeginCapture();
            clock.EndCapture(i * tick, i / 120.0, i * tick + lowLate);
        }
        Assert.True(clock.Delay >= spikedDelay - 1e-4, "Expected peak to remain active while spike is inside 128-slot ring");

        // Once the spike leaves the 128-tick ring, rescan drops peak and delay eases back down
        for (int i = 141; i <= 320; i++)
        {
            clock.BeginCapture();
            clock.EndCapture(i * tick, i / 120.0, i * tick + lowLate);
        }
        Assert.True(clock.Delay < spikedDelay - 0.001, $"Expected delay to decay after spike exited ring: before={spikedDelay}, after={clock.Delay}");
    }

    [Fact]
    public void AudioMixer_silent_fast_path_and_SIMD_1to1_stereo_mixing_match_reference()
    {
        const int rate = 48000;
        var mixer = new AudioMixer(rate);

        // Silent fast path clears output and returns false
        var buf = new float[64];
        Array.Fill(buf, 0.75f);
        Assert.False(mixer.Mix(buf));
        Assert.All(buf, sample => Assert.Equal(0.0f, sample));

        // Build a non-trivial stereo sound (> 8 samples to exercise Vector128 SIMD + odd tail)
        const int soundFrames = 19;
        var raw = new short[soundFrames * 2];
        for (int f = 0; f < soundFrames; f++)
        {
            raw[f * 2] = (short)(1000 + f * 500);
            raw[f * 2 + 1] = (short)(-2000 - f * 400);
        }
        var sound = new Sound(raw, rate);

        // Mix 45 frames looped (spans 2 full wraps + partial tail) with pan & volume
        const float volume = 0.6f;
        const float pan = -0.25f;
        SoundHandle handle = mixer.Play(sound, AudioBus.Effects, volume: volume, pitch: 1.0f, pan: pan, loop: true);

        var mixed = new float[45 * 2];
        Assert.True(mixer.Mix(mixed));
        Assert.True(mixer.IsPlaying(handle));

        float gainL = volume * MathF.Min(1.0f, 1.0f - pan) * Sound.SCALE;
        float gainR = volume * MathF.Min(1.0f, 1.0f + pan) * Sound.SCALE;
        for (int f = 0; f < 45; f++)
        {
            int srcFrame = f % soundFrames;
            float expectedL = Math.Clamp(raw[srcFrame * 2] * gainL, -1.0f, 1.0f);
            float expectedR = Math.Clamp(raw[srcFrame * 2 + 1] * gainR, -1.0f, 1.0f);
            Assert.Equal(expectedL, mixed[f * 2], 5);
            Assert.Equal(expectedR, mixed[f * 2 + 1], 5);
        }

        // Stopping all voices returns mixer to silent fast path
        mixer.StopAll();
        Assert.False(mixer.Mix(mixed));
        Assert.All(mixed, sample => Assert.Equal(0.0f, sample));
    }

    [Theory]
    [InlineData(typeof(SpriteTweens), "Punch")]
    [InlineData(typeof(SpriteTweens), "TweenPosition")]
    [InlineData(typeof(SpriteTweens), "Flash")]
    [InlineData(typeof(SpriteTweens), "Shake")]
    [InlineData(typeof(UITweens), "Punch")]
    [InlineData(typeof(UITweens), "TweenOffset")]
    [InlineData(typeof(UITweens), "TweenOpacity")]
    [InlineData(typeof(UITweens), "Shake")]
    [InlineData(typeof(Entity), "UpdatePhysics")]
    [InlineData(typeof(Entity), "UpdateState")]
    [InlineData(typeof(Entity), "Capture")]
    [InlineData(typeof(PhysicsFluidParticleGroup), "Step")]
    [InlineData(typeof(AudioMixer), "Mix")]
    public void Pass6_hot_paths_make_no_closures(Type type, string methodName)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MethodInfo[] methods = [.. type.GetMethods(flags).Where(m => m.Name == methodName)];
        Assert.NotEmpty(methods);

        foreach (MethodInfo method in methods)
        {
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;

            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x73) continue; // newobj
                MethodBase? ctor;
                try
                {
                    ctor = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                }
                catch (Exception)
                {
                    continue;
                }

                if (ctor?.DeclaringType is { } declared)
                {
                    Assert.False(declared.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal),
                        $"{type.Name}.{methodName} allocates closure {declared.Name}");
                }
            }
        }
    }

    private sealed class TestEntity : Entity { }

    private sealed class PhysicsOverrideEntity : Entity
    {
        public int PhysicsCalls { get; private set; }
        public override void UpdatePhysics(float dt)
        {
            PhysicsCalls++;
            base.UpdatePhysics(dt);
        }
    }

    private sealed class CountingComponent(ComponentCapabilities capabilities) : GameComponent
    {
        public override ComponentCapabilities Capabilities => capabilities;
        public int StateCalls { get; private set; }
        public int PhysicsCalls { get; private set; }
        public int CaptureCalls { get; private set; }

        public override void UpdateState(float dt) => StateCalls++;
        public override void UpdatePhysics(float dt) => PhysicsCalls++;
        public override void Capture() => CaptureCalls++;
    }

    private sealed class TestUIComponent : UIComponent { }
}
