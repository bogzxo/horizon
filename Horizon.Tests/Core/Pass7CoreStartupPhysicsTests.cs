using System.Numerics;

using Horizon.Audio;
using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine.Components;
using Horizon.Physics;
using Horizon.Physics.Fixtures;
using Horizon.Physics.Simulation;
using Horizon.Platform;
using Horizon.Platform.Desktop;
using Horizon.Rendering.Particles.Simulation;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Core;

public class Pass7CoreStartupPhysicsTests
{
    [Theory]
    [InlineData(1.0f / 60.0f)]
    [InlineData(1.0f / 120.0f)]
    [InlineData(1.0f / 240.0f)]
    public void Fluid_thin_walled_basin_stays_contained_and_uses_partitioned_dense_pairs(float dt)
    {
        const float gravity = 1200.0f;
        const int drops = 600, go = 100;

        var world = new PhysicsWorld { Gravity = new Vector2(0.0f, -gravity) };
        var ground = world.CreateBody(PhysicsBodySimulationType.Static);
        ground.CreateRectangularFixture(new Vector2(-800.0f, -350.0f), new Vector2(1600.0f, 50.0f));
        ground.CreateRectangularFixture(new Vector2(400.0f, -300.0f), new Vector2(10.0f, 160.0f));
        ground.CreateRectangularFixture(new Vector2(640.0f, -300.0f), new Vector2(10.0f, 160.0f));

        PhysicsFluidParticleGroup fluid = world.CreateFluidParticleGroup(2048);
        fluid.Radius = 3.0f;
        fluid.Friction = 1.0f;
        fluid.MaxLife = 0.0f;
        fluid.MaxAge = 1000.0f;
        fluid.Gravity = new Vector2(0.0f, -gravity);

        Span<ParticleState2D> batch = stackalloc ParticleState2D[go];
        int settleTicks = (int)MathF.Round(0.4f / dt);
        for (int pour = 0; pour < drops / go; pour++)
        {
            for (int i = 0; i < go; i++)
            {
                batch[i] = new ParticleState2D
                {
                    Position = new Vector2(470.0f + (i % 20) * 5.0f, -260.0f + (i / 20) * 5.0f),
                    Velocity = Vector2.Zero,
                    Life = 1.0f,
                    Rate = 1.0f
                };
            }
            fluid.Add(batch);

            for (int t = 0; t < settleTicks; t++)
                world.UpdatePhysics(dt);
        }

        int finalTicks = (int)MathF.Round(2.5f / dt);
        for (int t = 0; t < finalTicks; t++)
            world.UpdatePhysics(dt);

        Assert.True(fluid.LastStepUsedDenseGrid);
        Assert.True(fluid.LastPairCount > 0);
        Assert.True(fluid.LastClosePairCount <= fluid.LastPairCount);
        Assert.True(fluid.Count >= drops - 12, $"Expected at least {drops - 12} live particles, got {fluid.Count}");

        int inside = 0;
        float lowest = float.MaxValue, highest = float.MinValue;
        foreach (ref readonly ParticleState2D drop in fluid.Particles)
        {
            if (drop.Position.X < 405.0f || drop.Position.X > 645.0f) continue;
            inside++;
            lowest = MathF.Min(lowest, drop.Position.Y);
            highest = MathF.Max(highest, drop.Position.Y);
        }

        Assert.True(inside >= drops - 12, $"Expected at least {drops - 12} particles inside basin, got {inside}");
        Assert.True(lowest > -302.0f, $"Lowest particle fell below floor: {lowest}");
        Assert.True(highest - lowest < 120.0f, $"Fluid surface did not lie level: height={highest - lowest}");
    }

    [Fact]
    public void PhysicsStaticGrid_uses_rectangle_fast_path_and_deduplicates_multi_cell_shapes()
    {
        var grid = new PhysicsStaticGrid();
        var body = new PhysicsBodyComponent2D { SimulationType = PhysicsBodySimulationType.Static };
        // Single wide rectangle spanning multiple 32-unit grid cells [0..160] x [0..10]
        body.CreateRectangularFixture(new Vector2(0.0f, 0.0f), new Vector2(160.0f, 10.0f));
        grid.Refresh([body]);

        Assert.True(grid.AllRectangles);
        Assert.True(grid.Contains(new Vector2(32.0f, 5.0f)));
        Assert.False(grid.Contains(new Vector2(32.0f, 15.0f)));

        // Particle straddling the cell boundary at X = 32 (touches both cell 0 and cell 1)
        Vector2 pos = new(32.0f, 11.0f);
        Vector2 contact = default;
        grid.SeparateCircle(ref pos, 3.0f, ref contact);

        // Must be pushed out once by depth = 2.0 to Y = 13.0 (not double-pushed across both cells)
        Assert.Equal(32.0f, pos.X, 4);
        Assert.Equal(13.0f, pos.Y, 4);
        Assert.Equal(Vector2.UnitY, contact);

        // Segment crossing multiple cells hits the top face at t = 0.5
        Assert.True(grid.TryIntersectSegment(new Vector2(20.0f, 20.0f), new Vector2(44.0f, 0.0f), out float hitT, out Vector2 hitNormal));
        Assert.Equal(0.5f, hitT, 3);
        Assert.Equal(Vector2.UnitY, hitNormal);

        // Adding a circle disables AllRectangles while preserving correctness
        body.CreateCircleFixture(new Vector2(80.0f, 40.0f), 5.0f);
        grid.Refresh([body]);
        Assert.False(grid.AllRectangles);
        Assert.True(grid.Contains(new Vector2(80.0f, 40.0f)));
    }

    [Fact]
    public void AudioMixer_SetSampleRate_updates_rate_and_MixFloatStereo1To1Simd_accumulates_exact_gains()
    {
        var mixer = new AudioMixer(48000);
        Assert.Equal(48000, mixer.SampleRate);

        mixer.SetSampleRate(44100);
        Assert.Equal(44100, mixer.SampleRate);

        mixer.SetSampleRate(0);
        Assert.Equal(1, mixer.SampleRate);

        // Test SIMD + tail path (15 stereo frames = 30 floats: 3x Vector256 + 1x Vector128 + 1x scalar stereo pair)
        const int frames = 15;
        float[] src = new float[frames * 2];
        float[] dst = new float[frames * 2];
        for (int i = 0; i < src.Length; i++)
        {
            src[i] = (i + 1) * 0.1f;
            dst[i] = 0.25f;
        }

        const float gainL = 0.5f;
        const float gainR = 0.75f;
        AudioMixer.MixFloatStereo1To1Simd(src, dst, gainL, gainR);

        for (int f = 0; f < frames; f++)
        {
            Assert.Equal(0.25f + src[f * 2] * gainL, dst[f * 2], 5);
            Assert.Equal(0.25f + src[f * 2 + 1] * gainR, dst[f * 2 + 1], 5);
        }
    }

    [Fact]
    public void AudioDevice_async_open_creates_mixer_immediately_applies_rate_pause_and_dispose_race()
    {
        // Case 1: async open completes with a negotiated 44100 Hz rate and honors pending Pause()
        using var openGate = new ManualResetEventSlim(false);
        using var startedGate = new ManualResetEventSlim(false);
        var output = new BlockingFakeAudioOutput(openGate, startedGate, negotiatedRate: 44100);

        using (var device = new AudioDevice(output, openAsync: true))
        {
            // Mixer is available immediately at 48 kHz before Open() finishes
            Assert.Equal(48000, device.Mixer.SampleRate);
            Assert.False(device.IsOpen);

            // Pause while open is still in flight
            device.Pause();
            Assert.True(device.IsPaused);

            openGate.Set();
            Assert.True(startedGate.Wait(TimeSpan.FromSeconds(5)));

            // Wait briefly for post-Start lock exit to publish output and apply Pause()
            SpinWait.SpinUntil(() => device.IsOpen && output.Pauses == 1, TimeSpan.FromSeconds(5));
            Assert.True(device.IsOpen);
            Assert.Equal(44100, device.Mixer.SampleRate);
            Assert.Equal(44100, device.Format.SampleRate);
            Assert.Equal(1, output.Pauses);
        }

        Assert.Equal(1, output.Disposals);

        // Case 2: Dispose() called while Open() is still in flight must dispose the output once Open() returns
        using var raceGate = new ManualResetEventSlim(false);
        using var disposedGate = new ManualResetEventSlim(false);
        var racingOutput = new BlockingFakeAudioOutput(raceGate, null, negotiatedRate: 48000, onDispose: () => disposedGate.Set());

        var racingDevice = new AudioDevice(racingOutput, openAsync: true);
        racingDevice.Dispose();
        raceGate.Set();

        Assert.True(disposedGate.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, racingOutput.Starts);
        Assert.Equal(1, racingOutput.Disposals);
        Assert.False(racingDevice.IsOpen);
    }

    [Fact]
    public void SilkInputSource_AsciiStrings_caches_all_128_ascii_characters()
    {
        Assert.Equal(128, SilkInputSource.AsciiStrings.Length);
        for (int i = 0; i < 128; i++)
        {
            char c = (char)i;
            string s = SilkInputSource.CharToString(c);
            Assert.Same(SilkInputSource.AsciiStrings[i], s);
            Assert.Equal(c.ToString(), s);
        }

        Assert.Equal("é", SilkInputSource.CharToString('é'));
    }

    [Fact]
    public void LanguageServer_suffix_glob_fast_path_matches_extensions_and_falls_back_for_complex_globs()
    {
        Assert.True(LanguageServer.TryParseSuffixGlob("**/*.cs", anywhere: true, out string[]? cs));
        Assert.NotNull(cs);
        Assert.True(LanguageServer.MatchesSuffixes("src/Foo.cs", cs!));
        Assert.True(LanguageServer.MatchesSuffixes("FOO.CS", cs!));
        Assert.False(LanguageServer.MatchesSuffixes("src/Foo.csproj", cs!));

        Assert.True(LanguageServer.TryParseSuffixGlob("**/*.{cs,cshtml,csproj,sln,slnx}", anywhere: false, out string[]? dotnet));
        Assert.NotNull(dotnet);
        Assert.True(LanguageServer.MatchesSuffixes("a/b/Program.cs", dotnet!));
        Assert.True(LanguageServer.MatchesSuffixes("Horizon.slnx", dotnet!));
        Assert.False(LanguageServer.MatchesSuffixes("chat/stream.json", dotnet!));

        // Bare *.json is only a suffix glob when anywhere == true
        Assert.True(LanguageServer.TryParseSuffixGlob("*.json", anywhere: true, out string[]? json));
        Assert.True(LanguageServer.MatchesSuffixes("a/b/config.json", json!));
        Assert.False(LanguageServer.TryParseSuffixGlob("*.json", anywhere: false, out _));

        // Complex globs fall back to Regex
        Assert.False(LanguageServer.TryParseSuffixGlob("src/**/foo?.cs", anywhere: true, out _));
        Assert.False(LanguageServer.TryParseSuffixGlob("**/*.{cs,vb*}", anywhere: true, out _));
    }

    [Fact]
    public void Deferred_SilkGamepadSource_delays_probe_until_activated_and_skips_when_disposed()
    {
        int probeCount = 0;
        bool ready = false;
        var source = new SilkGamepadSource(
            onProbe: () => probeCount++,
            postToWindow: static action => action(),
            sampled: null,
            isProbeReady: () => ready);

        source.StartHere();
        source.Sample();
        Assert.False(source.IsProbeActivated);
        Assert.Equal(0, source.ConnectedCount);
        Assert.Equal(0, probeCount);

        ready = true;
        source.ActivateProbe();
        source.ActivateProbe();
        Assert.True(source.IsProbeActivated);
        Assert.Equal(1, probeCount);

        source.DisposeHere();

        // Disposing before activation must prevent probing
        int skippedProbes = 0;
        var cancelled = new SilkGamepadSource(
            onProbe: () => skippedProbes++,
            postToWindow: static action => action(),
            sampled: null,
            isProbeReady: static () => false);

        cancelled.StartHere();
        cancelled.DisposeHere();
        cancelled.ActivateProbe();
        Assert.Equal(0, skippedProbes);
        Assert.False(cancelled.IsProbeActivated);
    }

    [Fact]
    public void NativeAot_hot_classes_are_sealed()
    {
        Assert.True(typeof(WindowManager).IsSealed);
        Assert.True(typeof(SceneManager).IsSealed);
        Assert.True(typeof(PhysicsWorld).IsSealed);
        Assert.True(typeof(PhysicsBodyComponent2D).IsSealed);
        Assert.True(typeof(TransformComponent2D).IsSealed);
        Assert.True(typeof(TransformComponent3D).IsSealed);
        Assert.True(typeof(IntervalRunnerFixedStep).IsSealed);
        Assert.True(typeof(IntervalRunnerSubStep).IsSealed);
    }

    private sealed class BlockingFakeAudioOutput(
        ManualResetEventSlim openGate,
        ManualResetEventSlim? startedGate,
        int negotiatedRate,
        Action? onDispose = null) : IAudioOutput
    {
        public int Starts;
        public int Pauses;
        public int Disposals;

        public AudioFormat Open(AudioFormat wanted)
        {
            openGate.Wait(TimeSpan.FromSeconds(5));
            return wanted with { SampleRate = negotiatedRate };
        }

        public void Start(AudioRender render)
        {
            Interlocked.Increment(ref Starts);
            startedGate?.Set();
        }

        public void Pause() => Interlocked.Increment(ref Pauses);
        public void Resume() { }

        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            onDispose?.Invoke();
        }
    }
}
