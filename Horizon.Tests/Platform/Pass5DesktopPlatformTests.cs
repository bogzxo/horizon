using Horizon.Platform;
using Horizon.Platform.Desktop;
using Horizon.UI.Components;

namespace Horizon.Tests.Platform;

/// <summary>
/// Headless unit tests covering Pass 5 desktop platform fixes: atomic wake coalescing, SDL audio idle wait pacing,
/// bounded off-thread dispatcher/clipboard marshalling, Win32 DPI rect/scale validation, and buffered scroll tracing.
/// </summary>
public class Pass5DesktopPlatformTests
{
    [Fact]
    public void Wake_gate_coalesces_redundant_signals_until_consumed()
    {
        var gate = new WindowWakeGate();
        Assert.False(gate.IsPending);
        Assert.False(gate.TryConsume());

        Assert.True(gate.TrySignal());
        Assert.True(gate.IsPending);

        for (int i = 0; i < 100; i++)
            Assert.False(gate.TrySignal());

        Assert.True(gate.TryConsume());
        Assert.False(gate.IsPending);
        Assert.False(gate.TryConsume());

        // Ready to signal again on the next wait cycle
        Assert.True(gate.TrySignal());
        Assert.True(gate.TryConsume());
    }

    [Fact]
    public void Wake_gate_concurrent_producers_never_lose_a_wakeup()
    {
        var gate = new WindowWakeGate();
        using var wakeEvent = new AutoResetEvent(false);

        const int producers = 4;
        const int perProducer = 2500;
        const int total = producers * perProducer;

        int produced = 0;
        int observed = 0;
        int osSignals = 0;
        var done = new ManualResetEventSlim(false);

        Task consumer = Task.Run(() =>
        {
            while (true)
            {
                int current = Volatile.Read(ref produced);
                observed = current;
                if (current >= total)
                {
                    done.Set();
                    return;
                }

                // Reset wake gate BEFORE re-checking condition/waiting, matching DesktopWindow.Waiting.
                if (gate.TryConsume())
                    continue;

                if (Volatile.Read(ref produced) != current)
                    continue;

                wakeEvent.WaitOne(50);
            }
        });

        Task[] workers = new Task[producers];
        for (int p = 0; p < producers; p++)
        {
            workers[p] = Task.Run(() =>
            {
                for (int i = 0; i < perProducer; i++)
                {
                    Interlocked.Increment(ref produced);
                    if (gate.TrySignal())
                    {
                        Interlocked.Increment(ref osSignals);
                        wakeEvent.Set();
                    }
                }
            });
        }

        Task.WaitAll(workers);
        Assert.True(done.Wait(5000), $"Consumer missed a wakeup; observed {observed} of {total}.");
        consumer.GetAwaiter().GetResult();

        Assert.Equal(total, observed);
        Assert.InRange(osSignals, 1, total);
    }

    [Fact]
    public void Audio_pump_wait_is_long_when_paused_and_bounded_when_silent_or_full()
    {
        var format = new AudioFormat(SampleRate: 48000, Channels: 2, FramesPerBuffer: 512);

        Assert.Equal(SdlAudioOutput.PAUSED_WAIT_MS, SdlAudioOutput.ComputeWaitMs(paused: true, queueFull: false, silent: false, silentStreak: 0, format));
        Assert.Equal(SdlAudioOutput.PAUSED_WAIT_MS, SdlAudioOutput.ComputeWaitMs(paused: true, queueFull: true, silent: true, silentStreak: 10, format));

        int fullWait = SdlAudioOutput.ComputeWaitMs(paused: false, queueFull: true, silent: false, silentStreak: 0, format);
        Assert.InRange(fullWait, 2, 10);

        int firstSilent = SdlAudioOutput.ComputeWaitMs(paused: false, queueFull: false, silent: true, silentStreak: 1, format);
        int midSilent = SdlAudioOutput.ComputeWaitMs(paused: false, queueFull: false, silent: true, silentStreak: 2, format);
        int steadySilent = SdlAudioOutput.ComputeWaitMs(paused: false, queueFull: false, silent: true, silentStreak: 8, format);

        Assert.Equal(SdlAudioOutput.SILENT_INITIAL_WAIT_MS, firstSilent);
        Assert.True(midSilent >= firstSilent);
        Assert.Equal(SdlAudioOutput.SILENT_MAX_WAIT_MS, steadySilent);
        Assert.Equal(0, SdlAudioOutput.ComputeWaitMs(paused: false, queueFull: false, silent: false, silentStreak: 0, format));
    }

    [Fact]
    public void Audio_output_dispose_without_start_is_idempotent()
    {
        var output = new SdlAudioOutput();
        output.Pause();
        output.Resume();
        output.Dispose();
        output.Dispose();
    }

    [Fact]
    public void TryInvoke_runs_inline_before_binding_and_on_platform_thread()
    {
        int wakes = 0;
        var dispatcher = new PlatformDispatcher(() => Interlocked.Increment(ref wakes));

        Assert.True(dispatcher.TryInvoke(() => "unbound", 100, out string? unbound));
        Assert.Equal("unbound", unbound);
        Assert.Equal(0, wakes);

        dispatcher.BindToCurrentThread();
        int ranOn = 0;
        Assert.True(dispatcher.TryInvoke(() =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            return "same-thread";
        }, 100, out string? same));
        Assert.Equal("same-thread", same);
        Assert.Equal(Environment.CurrentManagedThreadId, ranOn);
        Assert.Equal(0, wakes);
    }

    [Fact]
    public async Task TryInvoke_marshals_off_thread_work_and_returns_result()
    {
        var woken = new ManualResetEventSlim(false);
        var dispatcher = new PlatformDispatcher(woken.Set);
        dispatcher.BindToCurrentThread();
        int platformThreadId = Environment.CurrentManagedThreadId;

        int executedOn = 0;
        Task<(bool Ok, string? Value)> worker = Task.Factory.StartNew(() =>
        {
            bool ok = dispatcher.TryInvoke(() =>
            {
                executedOn = Environment.CurrentManagedThreadId;
                return "clipboard-payload";
            }, 2000, out string? value);
            return (ok, value);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Assert.True(woken.Wait(2000));
        while (!worker.IsCompleted)
            dispatcher.Drain();

        var (ok, value) = await worker;
        Assert.True(ok);
        Assert.Equal("clipboard-payload", value);
        Assert.Equal(platformThreadId, executedOn);
    }

    [Fact]
    public async Task TryInvoke_times_out_and_abandons_unstarted_work_without_deadlock()
    {
        var dispatcher = new PlatformDispatcher(() => { });
        dispatcher.BindToCurrentThread();

        int ran = 0;
        bool ok = await Task.Factory.StartNew(
            () => dispatcher.TryInvoke(() => Interlocked.Increment(ref ran), timeoutMs: 40),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.False(ok);
        // Draining afterwards must not run the abandoned work item.
        dispatcher.Drain();
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task TryInvoke_returns_false_when_closed_before_or_during_wait()
    {
        var dispatcher = new PlatformDispatcher(() => { });
        dispatcher.BindToCurrentThread();

        var entered = new ManualResetEventSlim(false);
        Task<bool> waiting = Task.Factory.StartNew(() =>
        {
            entered.Set();
            return dispatcher.TryInvoke(() => { }, timeoutMs: 5000);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Assert.True(entered.Wait(2000));
        await Task.Delay(25);
        dispatcher.Close();

        Assert.False(await waiting);
        Assert.False(dispatcher.TryInvoke(() => "after-close", 100, out string? value));
        Assert.Null(value);
    }

    [Fact]
    public void Windows_clipboard_retry_delay_is_progressive_and_skips_sleep_on_last_attempt()
    {
        Assert.Equal(1, WindowsClipboard.RetryDelayMs(0, 8));
        Assert.Equal(2, WindowsClipboard.RetryDelayMs(1, 8));
        Assert.Equal(2, WindowsClipboard.RetryDelayMs(2, 8));
        Assert.Equal(5, WindowsClipboard.RetryDelayMs(3, 8));
        Assert.Equal(5, WindowsClipboard.RetryDelayMs(6, 8));
        Assert.Equal(0, WindowsClipboard.RetryDelayMs(7, 8));
        Assert.Equal(0, WindowsClipboard.RetryDelayMs(-1, 8));
    }

    [Fact]
    public void Dpi_scale_from_wparam_decodes_dpi_and_rejects_invalid_values()
    {
        static nint PackDpi(int dpiX, int dpiY) => (dpiY << 16) | (dpiX & 0xFFFF);

        Assert.Equal(1.0f, DesktopWindow.ScaleFromDpiWParam(PackDpi(96, 96)), 3);
        Assert.Equal(1.25f, DesktopWindow.ScaleFromDpiWParam(PackDpi(120, 120)), 3);
        Assert.Equal(1.5f, DesktopWindow.ScaleFromDpiWParam(PackDpi(144, 144)), 3);
        Assert.Equal(2.0f, DesktopWindow.ScaleFromDpiWParam(PackDpi(192, 192)), 3);
        Assert.Equal(1.5f, DesktopWindow.ScaleFromDpiWParam(PackDpi(0, 144)), 3);
        Assert.Equal(0.0f, DesktopWindow.ScaleFromDpiWParam(0));
    }

    [Fact]
    public void Dpi_suggested_rect_respects_state_guards_minimum_size_and_reentrancy()
    {
        // Normal move onto higher-DPI monitor applies suggested rect as-is when above minimum.
        Assert.True(DesktopWindow.TryComputeDpiSuggestedRect(
            left: 100, top: 200, right: 1700, bottom: 1100,
            minWidth: 720, minHeight: 480,
            isMaximized: false, isMinimized: false, isFullscreen: false, inDpiChange: false,
            out int x, out int y, out int w, out int h));
        Assert.Equal((100, 200, 1600, 900), (x, y, w, h));

        // Suggested rect smaller than scaled minimum size is clamped safely.
        Assert.True(DesktopWindow.TryComputeDpiSuggestedRect(
            left: 10, top: 20, right: 310, bottom: 220,
            minWidth: 720, minHeight: 480,
            isMaximized: false, isMinimized: false, isFullscreen: false, inDpiChange: false,
            out x, out y, out w, out h));
        Assert.Equal((10, 20, 720, 480), (x, y, w, h));

        // Maximized, minimized, fullscreen, and re-entrant DPI messages never trigger SetWindowPos.
        Assert.False(DesktopWindow.TryComputeDpiSuggestedRect(0, 0, 1000, 800, 480, 320, isMaximized: true, isMinimized: false, isFullscreen: false, inDpiChange: false, out _, out _, out _, out _));
        Assert.False(DesktopWindow.TryComputeDpiSuggestedRect(0, 0, 1000, 800, 480, 320, isMaximized: false, isMinimized: true, isFullscreen: false, inDpiChange: false, out _, out _, out _, out _));
        Assert.False(DesktopWindow.TryComputeDpiSuggestedRect(0, 0, 1000, 800, 480, 320, isMaximized: false, isMinimized: false, isFullscreen: true, inDpiChange: false, out _, out _, out _, out _));
        Assert.False(DesktopWindow.TryComputeDpiSuggestedRect(0, 0, 1000, 800, 480, 320, isMaximized: false, isMinimized: false, isFullscreen: false, inDpiChange: true, out _, out _, out _, out _));

        // Degenerate rects are rejected.
        Assert.False(DesktopWindow.TryComputeDpiSuggestedRect(100, 100, 100, 500, 480, 320, false, false, false, false, out _, out _, out _, out _));
        Assert.False(DesktopWindow.TryComputeDpiSuggestedRect(100, 500, 500, 100, 480, 320, false, false, false, false, out _, out _, out _, out _));
    }

    [Fact]
    public void UIWheel_trace_buffers_lines_and_flushes_on_explicit_flush_and_threshold()
    {
        string path = Path.Combine(Path.GetTempPath(), $"horizon-scroll-trace-test-{Guid.NewGuid():N}.txt");
        try
        {
            UIWheel.SetTracePathOverride(path);
            Assert.True(UIWheel.Tracing);

            UIWheel.Trace("line-one");
            UIWheel.Trace("line-two");

            // Small writes stay buffered until idle/threshold/flush rather than hitting disk immediately.
            Assert.False(File.Exists(path));

            UIWheel.FlushTrace();
            Assert.True(File.Exists(path));
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains(" ui line-one", lines[0]);
            Assert.Contains(" ui line-two", lines[1]);

            // Exceeding the buffer threshold triggers an automatic flush without needing FlushTrace().
            string bigLine = new('x', UIWheel.TraceFlushThresholdChars);
            UIWheel.Trace(bigLine);

            string content = File.ReadAllText(path);
            Assert.Contains(bigLine, content);
        }
        finally
        {
            UIWheel.SetTracePathOverride(null);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void UIWheel_trace_auto_flushes_after_idle_delay()
    {
        string path = Path.Combine(Path.GetTempPath(), $"horizon-scroll-trace-idle-{Guid.NewGuid():N}.txt");
        try
        {
            UIWheel.SetTracePathOverride(path);
            UIWheel.Trace("idle-flush-line");

            // Wait for the idle flush timer (250 ms) with generous margin.
            bool flushed = SpinWait.SpinUntil(() => File.Exists(path) && File.ReadAllText(path).Contains("idle-flush-line", StringComparison.Ordinal), 2000);
            Assert.True(flushed);
        }
        finally
        {
            UIWheel.SetTracePathOverride(null);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
