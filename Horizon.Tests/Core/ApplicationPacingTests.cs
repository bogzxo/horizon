using System.Diagnostics;

using Horizon.Core;
using Horizon.Core.Threading;
using Horizon.UI.Components;

namespace Horizon.Tests;

/// <summary>
/// Covers application calm/smooth pacing, Windows high-resolution timer selection, and zero-allocation hot paths.
/// </summary>
public class ApplicationPacingTests
{
    private static long Seconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void Calm_returns_zero_while_RequestSmooth_is_active_and_resumes_after_expiry()
    {
        long now = Seconds(10.0);
        long oldInput = now - Seconds(WindowManager.IDLE_AFTER + 1.0);
        long smoothUntil = 0;

        // Before smooth is requested, calm returns 1/24 focused and 1/10 unfocused
        Assert.Equal(WindowManager.CALM_PERIOD, WindowManager.CalmPeriodFor(now, oldInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: true), 6);
        Assert.Equal(WindowManager.CALM_PERIOD_BEHIND, WindowManager.CalmPeriodFor(now, oldInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: false), 6);

        // RequestSmooth transitions from inactive -> active (returns true) and holds CalmNow at 0
        bool woke = WindowManager.ExtendDeadline(ref smoothUntil, now, 0.05);
        Assert.True(woke);
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: true));
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: false));

        // Extending again while still active returns false (no redundant wake)
        long mid = now + Seconds(0.02);
        bool wokeAgain = WindowManager.ExtendDeadline(ref smoothUntil, mid, 0.05);
        Assert.False(wokeAgain);
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(mid, oldInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: true));

        // Once the smooth window expires, calm pacing resumes immediately
        long after = mid + Seconds(0.06);
        Assert.Equal(WindowManager.CALM_PERIOD, WindowManager.CalmPeriodFor(after, oldInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: true), 6);
        Assert.Equal(WindowManager.CALM_PERIOD_BEHIND, WindowManager.CalmPeriodFor(after, oldInput, smoothUntil, exclusive: false, lifecycleWork: false, minimized: false, focused: false), 6);
    }

    [Fact]
    public void Calm_returns_zero_for_recent_input_exclusive_work_lifecycle_work_and_minimized()
    {
        long now = Seconds(10.0);
        long recentInput = now - Seconds(WindowManager.IDLE_AFTER * 0.5);
        long oldInput = now - Seconds(WindowManager.IDLE_AFTER + 0.1);

        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, recentInput, 0, exclusive: false, lifecycleWork: false, minimized: false, focused: true));
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, 0, exclusive: true, lifecycleWork: false, minimized: false, focused: true));
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, 0, exclusive: false, lifecycleWork: true, minimized: false, focused: true));
        Assert.Equal(0.0, WindowManager.CalmPeriodFor(now, oldInput, 0, exclusive: false, lifecycleWork: false, minimized: true, focused: true));
    }

    [Fact]
    public void Urgent_frames_shorten_a_frame_limit_and_never_lengthen_one()
    {
        const double LIMIT = 1.0 / 120.0, URGENT = 1.0 / 480.0;

        // Not urgent, the display's limit as it is
        Assert.Equal(LIMIT, WindowManager.FramePeriodFor(LIMIT, URGENT, urgent: false));

        // Urgent, the shorter of the two, and no limit on urgent frames is none at all
        Assert.Equal(URGENT, WindowManager.FramePeriodFor(LIMIT, URGENT, urgent: true));
        Assert.Equal(0.0, WindowManager.FramePeriodFor(LIMIT, 0.0, urgent: true));
        Assert.Equal(1.0 / 960.0, WindowManager.FramePeriodFor(1.0 / 960.0, URGENT, urgent: true));

        // A window with no limit has none while urgent either
        Assert.Equal(0.0, WindowManager.FramePeriodFor(0.0, URGENT, urgent: true));
    }

    [Fact]
    public void Calm_frame_window_covers_calm_pacing_interval_for_focused_and_unfocused()
    {
        // CalmFrameWindow must be at least PaceCalmFrames' half-period (calm * 0.5) so calm frames never expire before being drawn
        Assert.True(WindowManager.CalmFrameWindow(WindowManager.CALM_PERIOD) >= WindowManager.CALM_PERIOD * 0.5);
        Assert.True(WindowManager.CalmFrameWindow(WindowManager.CALM_PERIOD_BEHIND) >= WindowManager.CALM_PERIOD_BEHIND * 0.5);
        Assert.True(WindowManager.CalmFrameWindow(0.0) > 0.0);
    }

    [Fact]
    public void LoopTiming_only_sharpens_global_timer_when_precise_or_without_high_res_timer()
    {
        if (OperatingSystem.IsWindows())
        {
            // Games (precisePacing: true) always sharpen the global timer
            Assert.True(LoopTiming.NeedsSharpTimer(precisePacing: true, highResolutionTimerAvailable: true));
            Assert.True(LoopTiming.NeedsSharpTimer(precisePacing: true, highResolutionTimerAvailable: false));

            // Applications (precisePacing: false) skip timeBeginPeriod(1) when high-res waitable timers are available
            Assert.False(LoopTiming.NeedsSharpTimer(precisePacing: false, highResolutionTimerAvailable: true));

            // Fallback on older Windows without high-res waitable timers still sharpens the global timer
            Assert.True(LoopTiming.NeedsSharpTimer(precisePacing: false, highResolutionTimerAvailable: false));
        }
        else
        {
            Assert.False(LoopTiming.NeedsSharpTimer(precisePacing: true, highResolutionTimerAvailable: false));
            Assert.False(LoopTiming.NeedsSharpTimer(precisePacing: false, highResolutionTimerAvailable: false));
        }
    }

    [Fact]
    public void Non_precise_simulation_loop_runs_without_sharpening_when_high_res_timer_available()
    {
        using var ticked = new ManualResetEventSlim();
        using var loop = new SimulationLoop("App pacing test", 120, 120, new TickSignal(ticked))
        {
            PrecisePacing = false,
            StretchWhenBehind = true,
            CalmPeriod = () => WindowManager.CALM_PERIOD
        };

        loop.Start();
        try
        {
            Assert.True(ticked.Wait(TimeSpan.FromSeconds(2)));
            if (OperatingSystem.IsWindows() && LoopTiming.HasHighResolutionTimer)
                Assert.False(loop.SharpenedTimer);
        }
        finally
        {
            loop.Stop();
        }
    }

    [Fact]
    public void Pacing_and_scroll_ease_hot_paths_allocate_nothing()
    {
        long now = Seconds(10.0);
        long oldInput = now - Seconds(2.0);
        long smoothUntil = 0;
        var ease = new UIScrollEase();
        ease.Wheel(0.0f, 100.0f, 500.0f);

        using var loop = new SimulationLoop(120, 120, new NoopHost(), () => now);

        // Warm up JIT before measuring allocations
        _ = WindowManager.CalmPeriodFor(now, oldInput, smoothUntil, false, false, false, true);
        _ = WindowManager.ExtendDeadline(ref smoothUntil, now, 0.05);
        _ = ease.Step(0.0f, 1.0f / 120.0f);
        loop.Step();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
        {
            now += Seconds(1.0 / 120.0);
            _ = WindowManager.CalmPeriodFor(now, oldInput, smoothUntil, false, false, false, true);
            _ = WindowManager.ExtendDeadline(ref smoothUntil, now, 0.05);
            _ = ease.Step(10.0f, 1.0f / 120.0f);
            loop.Step();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private sealed class TickSignal(ManualResetEventSlim signal) : ISimulationHost
    {
        public void BeginTick() { }
        public void UpdateState(float dt) => signal.Set();
        public void UpdatePhysics(float dt) { }
        public void EndTick(long tick, long stamp, double time) { }
    }

    private sealed class NoopHost : ISimulationHost
    {
        public void BeginTick() { }
        public void UpdateState(float dt) { }
        public void UpdatePhysics(float dt) { }
        public void EndTick(long tick, long stamp, double time) { }
    }
}
