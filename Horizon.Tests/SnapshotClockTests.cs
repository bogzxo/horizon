using System.Diagnostics;

using Horizon.Core.Threading;

namespace Horizon.Tests;

public class SnapshotClockTests
{
    private static readonly long Tick = Stopwatch.Frequency / 120;

    private static void Publish(SnapshotClock clock, Snapshot<int> state, int value, long stamp, long publishedAt = 0)
    {
        clock.BeginCapture();
        Assert.True(state.Publish(clock, value));
        clock.EndCapture(stamp, value / 120.0, publishedAt == 0 ? stamp : publishedAt);
    }

    [Fact]
    public void Before_the_first_publish_a_frame_has_nothing_to_draw()
    {
        var clock = new SnapshotClock();
        RenderFrame frame = clock.Acquire(PresentationMode.Interpolated, Tick);

        Assert.False(frame.HasSnapshot);
        clock.Release();
    }

    [Fact]
    public void A_frame_holds_the_newest_snapshot_and_the_one_before_it()
    {
        var clock = new SnapshotClock();
        var state = new Snapshot<int>();

        Publish(clock, state, 1, Tick);
        Publish(clock, state, 2, Tick * 2);
        Publish(clock, state, 3, Tick * 3);

        RenderFrame frame = clock.Acquire(PresentationMode.Interpolated, Tick * 4);
        Assert.True(state.TryGet(frame, out int previous, out int current, out bool continuous));
        Assert.Equal(2, previous);
        Assert.Equal(3, current);
        Assert.True(continuous);
        clock.Release();
    }

    [Fact]
    public void Latest_shows_the_newest_snapshot_as_it_is()
    {
        var clock = new SnapshotClock();
        var state = new Snapshot<int>();

        Publish(clock, state, 1, Tick);
        Publish(clock, state, 2, Tick * 2);

        RenderFrame frame = clock.Acquire(PresentationMode.Latest, Tick * 2);
        Assert.Equal(1.0f, frame.Alpha);
        Assert.Equal(frame.PreviousSlot, frame.CurrentSlot);
        Assert.True(state.TryGet(frame, out int current));
        Assert.Equal(2, current);
        clock.Release();
    }

    [Fact]
    public void The_slots_a_frame_holds_are_never_written_to()
    {
        var clock = new SnapshotClock();
        var state = new Snapshot<int>();

        Publish(clock, state, 1, Tick);
        Publish(clock, state, 2, Tick * 2);

        RenderFrame frame = clock.Acquire(PresentationMode.Interpolated, Tick * 2);

        // The simulation goes on and on while the frame is drawn, a slow frame
        for (int i = 3; i < 50; i++)
            Publish(clock, state, i, Tick * i);

        Assert.True(state.TryGet(frame, out int previous, out int current, out _));
        Assert.Equal(1, previous);
        Assert.Equal(2, current);
        clock.Release();

        // The next frame is drawn from the newest there is, and whatever whole snapshot is left before it
        RenderFrame next = clock.Acquire(PresentationMode.Interpolated, Tick * 50);
        Assert.True(state.TryGet(next, out previous, out current, out _));
        Assert.Equal(49, current);
        Assert.True(previous < current);
        clock.Release();
    }

    [Fact]
    public void Steady_ticks_are_shown_at_a_steady_pace()
    {
        var clock = new SnapshotClock();
        var state = new Snapshot<int>();

        // Ticks that come exactly on time, published a little after the moment they stand for
        long lateness = Tick / 10;
        int published = 0;
        double lastShown = double.NaN;
        var steps = new List<double>();

        // Frames at a rate that doesn't line up with the ticks at all
        long frame = Stopwatch.Frequency / 144;
        for (long now = Tick; now < Tick * 600; now += frame)
        {
            while ((published + 1) * Tick + lateness <= now)
            {
                published++;
                Publish(clock, state, published, published * Tick, published * Tick + lateness);
            }

            RenderFrame shown = clock.Acquire(PresentationMode.Interpolated, now);
            if (shown.HasSnapshot && published > 60)
            {
                if (!double.IsNaN(lastShown)) steps.Add(shown.PresentationTime - lastShown);
                lastShown = shown.PresentationTime;
            }

            clock.Release();
        }

        // Every frame moves the shown time on by as much as real time went by, give or take a whisker
        double expected = 1.0 / 144.0;
        Assert.All(steps, step => Assert.InRange(step, expected * 0.9, expected * 1.1));
    }

    [Fact]
    public void A_break_is_not_blended_across()
    {
        var clock = new SnapshotClock();
        var state = new Snapshot<Position>();

        clock.BeginCapture();
        state.Publish(clock, new Position(0.0f));
        clock.EndCapture(Tick, 0.0, Tick);

        state.Break();

        clock.BeginCapture();
        state.Publish(clock, new Position(1000.0f));
        clock.EndCapture(Tick * 2, 1.0 / 120.0, Tick * 2);

        // Halfway between the two
        RenderFrame frame = clock.Acquire(PresentationMode.Interpolated, Tick * 2 + Tick / 2) with { Alpha = 0.5f };

        Assert.True(state.TryGet(frame, out _, out _, out bool continuous));
        Assert.False(continuous);
        Assert.True(state.TryBlend(frame, out Position shown));
        Assert.Equal(0.0f, shown.X);

        // All the way there it is where it was put
        Assert.True(state.TryBlend(frame with { Alpha = 1.0f }, out shown));
        Assert.Equal(1000.0f, shown.X);
        clock.Release();
    }

    [Fact]
    public void Something_that_is_new_is_shown_as_it_is()
    {
        var clock = new SnapshotClock();
        var old = new Snapshot<Position>();
        var spawned = new Snapshot<Position>();

        clock.BeginCapture();
        old.Publish(clock, new Position(0.0f));
        clock.EndCapture(Tick, 0.0, Tick);

        clock.BeginCapture();
        old.Publish(clock, new Position(10.0f));
        spawned.Publish(clock, new Position(500.0f));
        clock.EndCapture(Tick * 2, 1.0 / 120.0, Tick * 2);

        RenderFrame frame = clock.Acquire(PresentationMode.Interpolated, Tick * 2) with { Alpha = 0.5f };

        Assert.True(old.TryBlend(frame, out Position shown));
        Assert.Equal(5.0f, shown.X);
        Assert.True(spawned.TryBlend(frame, out shown));
        Assert.Equal(500.0f, shown.X);
        clock.Release();
    }

    [Fact]
    public void Something_that_was_not_published_is_not_there()
    {
        var clock = new SnapshotClock();
        var shown = new Snapshot<Position>();
        var hidden = new Snapshot<Position>();

        clock.BeginCapture();
        shown.Publish(clock, new Position(1.0f));
        hidden.Publish(clock, new Position(1.0f));
        clock.EndCapture(Tick, 0.0, Tick);

        // Switched off: no longer published
        clock.BeginCapture();
        shown.Publish(clock, new Position(2.0f));
        clock.EndCapture(Tick * 2, 1.0 / 120.0, Tick * 2);

        RenderFrame frame = clock.Acquire(PresentationMode.Interpolated, Tick * 2);
        Assert.True(shown.TryGet(frame, out _));
        Assert.False(hidden.TryGet(frame, out _));
        clock.Release();
    }

    [Fact]
    public void Publishing_outside_of_a_capture_keeps_nothing()
    {
        var clock = new SnapshotClock();
        var state = new Snapshot<int>();

        Assert.False(state.Publish(clock, 5));
        Assert.False(state.Publish(null, 5));
    }

    [Fact]
    public void Drawing_and_simulating_at_once_never_sees_half_a_snapshot()
    {
        var clock = new SnapshotClock();
        var state = new Snapshot<Pair>();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        long torn = 0, backwards = 0, frames = 0;

        var simulation = new Thread(() =>
        {
            long tick = 0;
            while (!stop.IsCancellationRequested)
            {
                tick++;
                clock.BeginCapture();
                state.Publish(clock, new Pair(tick, -tick));
                clock.EndCapture(tick, tick, Stopwatch.GetTimestamp());
            }
        });

        var render = new Thread(() =>
        {
            long last = 0;
            while (!stop.IsCancellationRequested)
            {
                RenderFrame frame = clock.Acquire(PresentationMode.Interpolated);
                if (state.TryGet(frame, out Pair previous, out Pair current, out _))
                {
                    frames++;

                    // Spin a little, the simulation keeps on writing meanwhile
                    Thread.SpinWait(200);

                    if (current.A != -current.B || previous.A != -previous.B) torn++;
                    if (current.A < last || previous.A > current.A) backwards++;
                    if (current.A != frame.CurrentSequence) torn++;
                    last = current.A;

                    // Read again after the spin: still the same, nothing wrote over it
                    if (!state.TryGet(frame, out Pair again) || again != current) torn++;
                }

                clock.Release();
            }
        });

        simulation.Start();
        render.Start();
        simulation.Join();
        render.Join();

        Assert.True(frames > 100, $"only {frames} frames were drawn");
        Assert.Equal(0, torn);
        Assert.Equal(0, backwards);
    }

    private readonly record struct Pair(long A, long B);

    private readonly record struct Position(float X) : IBlendable<Position>
    {
        public static Position Blend(in Position from, in Position to, float amount) => new(Interpolate.Linear(from.X, to.X, amount));
    }
}
