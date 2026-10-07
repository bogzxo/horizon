using System.Diagnostics;

using Horizon.Core.Threading;

namespace Horizon.Tests;

public class SimulationLoopTests
{
    private sealed class Recorder : ISimulationHost
    {
        public readonly List<string> Calls = [];
        public readonly List<float> LogicDeltas = [], PhysicsDeltas = [];
        public readonly List<long> Stamps = [];

        public void BeginTick() { }

        public void UpdateState(float dt)
        {
            Calls.Add("logic");
            LogicDeltas.Add(dt);
        }

        public void UpdatePhysics(float dt)
        {
            Calls.Add("physics");
            PhysicsDeltas.Add(dt);
        }

        public void EndTick(long tick, long stamp, double time)
        {
            Calls.Add("end");
            Stamps.Add(stamp);
        }
    }

    private static long Seconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void A_tick_is_logic_then_physics_then_its_end()
    {
        var host = new Recorder();
        long now = Seconds(1.0);
        var loop = new SimulationLoop(120, 120, host, () => now);

        loop.Step();

        Assert.Equal(["logic", "physics", "end"], host.Calls);
        Assert.Equal(1, loop.Tick);
    }

    [Fact]
    public void Equal_rates_give_every_tick_one_of_each_with_a_fixed_step()
    {
        var host = new Recorder();
        long now = Seconds(1.0);
        var loop = new SimulationLoop(120, 120, host, () => now);

        for (int i = 0; i < 120; i++)
        {
            loop.Step();
            now += Seconds(1.0 / 120.0);
        }

        Assert.Equal(120, host.LogicDeltas.Count);
        Assert.Equal(120, host.PhysicsDeltas.Count);
        Assert.All(host.LogicDeltas, dt => Assert.Equal(1.0f / 120.0f, dt, 1e-6f));
        Assert.All(host.PhysicsDeltas, dt => Assert.Equal(1.0f / 120.0f, dt, 1e-6f));
        Assert.Equal(1.0, loop.Time, 6);
    }

    [Fact]
    public void Logic_slower_than_the_physics_is_told_how_long_it_has_been()
    {
        var host = new Recorder();
        long now = Seconds(1.0);
        var loop = new SimulationLoop(60, 120, host, () => now);

        for (int i = 0; i < 120; i++)
        {
            loop.Step();
            now += Seconds(1.0 / 120.0);
        }

        Assert.Equal(120, host.PhysicsDeltas.Count);
        Assert.Equal(60, host.LogicDeltas.Count);
        Assert.All(host.LogicDeltas, dt => Assert.Equal(1.0f / 60.0f, dt, 1e-5f));
    }

    [Fact]
    public void Physics_slower_than_the_logic_steps_at_its_own_rate()
    {
        var host = new Recorder();
        long now = Seconds(1.0);
        var loop = new SimulationLoop(240, 60, host, () => now);

        for (int i = 0; i < 240; i++)
        {
            loop.Step();
            now += Seconds(1.0 / 240.0);
        }

        Assert.Equal(240, host.LogicDeltas.Count);
        Assert.Equal(60, host.PhysicsDeltas.Count);
        Assert.All(host.PhysicsDeltas, dt => Assert.Equal(1.0f / 60.0f, dt, 1e-6f));
    }

    [Fact]
    public void Ticks_are_stamped_with_when_they_were_due()
    {
        var host = new Recorder();
        long start = Seconds(1.0), now = start;
        var loop = new SimulationLoop(100, 100, host, () => now);

        for (int i = 0; i < 10; i++)
        {
            loop.Step();

            // Woken a little late every time, which isn't when the tick stands for
            now += Seconds(0.01) + (i % 2 == 0 ? Seconds(0.002) : 0);
        }

        for (int i = 1; i < host.Stamps.Count; i++)
            Assert.InRange(host.Stamps[i] - host.Stamps[i - 1], Seconds(0.01) - 2, Seconds(0.01) + 2);
    }

    [Fact]
    public void Falling_behind_makes_up_for_a_few_ticks_and_lets_the_rest_go()
    {
        var host = new Recorder();
        long now = Seconds(1.0);
        var loop = new SimulationLoop(100, 100, host, () => now);

        loop.Step();
        int before = host.LogicDeltas.Count;

        // A long stall: a whole second
        now += Seconds(1.0);
        loop.Step();

        int made = host.LogicDeltas.Count - before;
        Assert.InRange(made, 1, 4);
        Assert.True(loop.Ticks.DroppedTurns > 90);

        // And it carries on at its rate from there, not in a rush
        now += Seconds(0.01);
        int afterStall = host.LogicDeltas.Count;
        loop.Step();
        Assert.Equal(1, host.LogicDeltas.Count - afterStall);
    }

    [Fact]
    public void Resynchronizing_forgets_the_time_that_stood_still()
    {
        var host = new Recorder();
        long now = Seconds(1.0);
        var loop = new SimulationLoop(100, 100, host, () => now);

        loop.Step();
        now += Seconds(0.5);
        loop.Resynchronize();

        int before = host.LogicDeltas.Count;
        loop.Step();
        Assert.Equal(1, host.LogicDeltas.Count - before);
        Assert.Equal(0, loop.Ticks.DroppedTurns);
    }

    private sealed class StandsStill(Func<SimulationLoop> loop, Action standStill) : ISimulationHost
    {
        public int Ticks;
        public bool Once = true;

        public void BeginTick() { }
        public void UpdateState(float dt) { }
        public void UpdatePhysics(float dt) { }

        public void EndTick(long tick, long stamp, double time)
        {
            Ticks++;
            if (!Once) return;

            // A scene set up at the end of the tick, which the loop is told it stood still for
            Once = false;
            standStill();
            loop().Resynchronize();
        }
    }

    [Fact]
    public void Resynchronizing_inside_of_a_tick_lets_the_catching_up_go()
    {
        long now = Seconds(1.0);
        SimulationLoop loop = null!;
        var host = new StandsStill(() => loop, () => now += Seconds(1.0)) { Once = false };
        loop = new SimulationLoop(100, 100, host, () => now);

        loop.Step();

        // Behind by a few ticks, and the first of them stands still for a second
        now += Seconds(0.035);
        host.Once = true;
        host.Ticks = 0;
        loop.Step();
        Assert.Equal(1, host.Ticks);

        // Carried on from after the stand still, one tick and then at the rate again
        loop.Step();
        Assert.Equal(2, host.Ticks);
        Assert.Equal(0, loop.Ticks.DroppedTurns);

        now += Seconds(0.01);
        loop.Step();
        Assert.Equal(3, host.Ticks);
    }
}

public class SimulationContextTests
{
    private sealed class Idle : ISimulationHost
    {
        public void BeginTick() { }
        public void UpdateState(float dt) { }
        public void UpdatePhysics(float dt) { }
        public void EndTick(long tick, long stamp, double time) { }
    }

    private static long Seconds(double seconds) => (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

    [Fact]
    public void What_is_posted_from_elsewhere_runs_at_the_start_of_the_next_tick_on_the_loop()
    {
        long now = Seconds(1.0);
        var loop = new SimulationLoop(100, 100, new Idle(), () => now);

        int ran = 0, ranOn = -1;
        var poster = new Thread(() => loop.Context.Post(_ =>
        {
            ran++;
            ranOn = Environment.CurrentManagedThreadId;
        }, null));
        poster.Start();
        poster.Join();

        Assert.Equal(0, ran);

        loop.Step();
        Assert.Equal(1, ran);
        Assert.Equal(Environment.CurrentManagedThreadId, ranOn);

        // Once, not every tick from then on
        now += Seconds(0.01);
        loop.Step();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void What_is_posted_while_running_waits_for_the_tick_after()
    {
        long now = Seconds(1.0);
        var loop = new SimulationLoop(100, 100, new Idle(), () => now);

        int ran = 0;
        void Again(object? _)
        {
            ran++;
            loop.Context.Post(Again, null);
        }

        loop.Context.Post(Again, null);
        loop.Step();
        Assert.Equal(1, ran);

        now += Seconds(0.01);
        loop.Step();
        Assert.Equal(2, ran);
    }

    [Fact]
    public async Task Awaiting_a_tween_carries_on_on_the_loop()
    {
        long now = Seconds(1.0);
        var loop = new SimulationLoop(100, 100, new Idle(), () => now);

        float value = 0.0f;
        var context = new Horizon.Core.Tweening.TweenContext();
        var tween = context.Play(Horizon.Core.Tweening.Tween.To(() => value, v => value = v, 1.0f, 0.05f));

        int carriedOnAt = -1;
        bool carriedOn = false;
        async Task WaitForIt()
        {
            await tween.AwaitCompleteOrKill();
            carriedOn = true;
            carriedOnAt = Environment.CurrentManagedThreadId;
        }

        // Started the way the simulation thread would start it: with its context current
        SynchronizationContext? before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(loop.Context);
        Task waiting = WaitForIt();
        SynchronizationContext.SetSynchronizationContext(before);

        context.Tick(0.1f);
        Assert.Equal(1.0f, value);

        // Done, but carried on with only when the loop gets to it
        await Task.Delay(50);
        Assert.False(carriedOn);

        loop.Step();
        Assert.True(carriedOn);
        Assert.Equal(Environment.CurrentManagedThreadId, carriedOnAt);
        await waiting;
    }
}
