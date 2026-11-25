using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Horizon.Core.Threading;

/// <summary>
/// Helper class for keeping time on a thread that has to do something at set moments (the ticks of the simulation,
/// the frames of a window with a limit on them). The thread sleeps for as long as it safely can and only spins for the
/// last stretch, where a sleep can't be trusted to end on time. So a thread with little to do costs little, rather
/// than a whole core spent on asking what time it is.
/// </summary>
internal static class LoopTiming
{
    // How close to the moment (in seconds) the thread stops sleeping and starts spinning. A sleep of a millisecond
    // is the shortest there is, and it can run a little over
    private const double SPIN_MARGIN = 0.0018;

    // How many keep time and want the system's timer sharp, see SharpenTimer
    private static int timerUsers;

    /// <summary>
    /// Waits for a moment (in seconds of the stopwatch), asleep for most of it, awake for the end.
    /// </summary>
    public static void WaitUntil(double moment, bool spin = true)
    {
        double frequency = Stopwatch.Frequency;

        // On Windows a timer that wakes to a fraction of a millisecond does most of the wait, so only the last
        // little bit is spun. Sleeping a millisecond at a time left up to two of them to spin, every tick and every
        // frame, which was most of what an editor sitting there blinking its caret cost
        nint timer = PreciseTimer();
        if (timer != 0)
        {
            double sleepFor = moment - Stopwatch.GetTimestamp() / frequency - (spin ? PRECISE_MARGIN : 0.0);
            if (sleepFor > 0.0)
            {
                long due = -(long)(sleepFor * 10_000_000.0);
                if (SetWaitableTimer(timer, ref due, 0, 0, 0, false) != 0)
                    WaitForSingleObject(timer, uint.MaxValue);
            }
        }

        while (true)
        {
            double left = moment - Stopwatch.GetTimestamp() / frequency;
            if (left <= 0.0) return;

            if (!spin)
            {
                // When the per-thread high-resolution waitable timer is available and timeBeginPeriod(1) is off,
                // avoid falling into Thread.Sleep(1) (which sleeps ~15.6 ms on Windows' default clock).
                if (timer != 0)
                {
                    if (left <= PRECISE_MARGIN) return;
                    long due = -(long)(left * 10_000_000.0);
                    if (SetWaitableTimer(timer, ref due, 0, 0, 0, false) != 0)
                    {
                        WaitForSingleObject(timer, uint.MaxValue);
                        continue;
                    }
                }

                // Desktop applications can tolerate a timer's small overshoot. Burning the last fraction of every
                // tick/frame on a core is useful for a game, but costs more than drawing an idle editor's caret.
                Thread.Sleep((int)Math.Min(int.MaxValue, Math.Ceiling(left * 1000.0)));
            }
            else if (left > SPIN_MARGIN) Thread.Sleep(1);
            else Thread.SpinWait(32);
        }
    }

    // How close to the moment the precise timer is told to wake, it can run over by about this much
    private const double PRECISE_MARGIN = 0.0006;

    // The precise timer of this thread, made the first time it waits, 0 where there isn't one (before Windows 10 1803,
    // or anywhere else), and whether that was found out already
    [ThreadStatic] private static nint preciseTimer;
    [ThreadStatic] private static bool preciseTimerTried;

    private static nint PreciseTimer()
    {
        if (preciseTimerTried || !OperatingSystem.IsWindows())
            return preciseTimer;

        preciseTimerTried = true;
        try
        {
            preciseTimer = CreateWaitableTimerExW(0, 0, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        }
        catch (Exception)
        {
            preciseTimer = 0;
        }

        return preciseTimer;
    }

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    // Process-wide cached probe of whether Windows supports CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Win10 1803+).
    private static int highResolutionTimerState; // 0 = unknown, 1 = available, -1 = unavailable

    /// <summary>
    /// Whether this OS provides per-thread high-resolution waitable timers (Win10 1803+ on x64 and ARM64), allowing
    /// desktop applications to sleep with sub-millisecond accuracy without raising the global OS timer interrupt.
    /// </summary>
    internal static bool HasHighResolutionTimer
    {
        get
        {
            int state = Volatile.Read(ref highResolutionTimerState);
            if (state != 0)
                return state > 0;

            bool available = false;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    nint probe = CreateWaitableTimerExW(0, 0, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
                    if (probe != 0)
                    {
                        available = true;
                        CloseHandle(probe);
                    }
                }
                catch (Exception)
                {
                    available = false;
                }
            }

            Volatile.Write(ref highResolutionTimerState, available ? 1 : -1);
            return available;
        }
    }

    /// <summary>
    /// Whether a loop with the given pacing mode should call <see cref="SharpenTimer"/> (<c>timeBeginPeriod(1)</c>).
    /// Games (<paramref name="precisePacing"/> true) keep the system timer sharp; desktop applications skip it when
    /// high-resolution waitable timers are available so Windows laptops don't stay locked at a 1000 Hz global interrupt.
    /// </summary>
    internal static bool NeedsSharpTimer(bool precisePacing) =>
        NeedsSharpTimer(precisePacing, HasHighResolutionTimer);

    internal static bool NeedsSharpTimer(bool precisePacing, bool highResolutionTimerAvailable) =>
        OperatingSystem.IsWindows() && (precisePacing || !highResolutionTimerAvailable);

    /// <summary>Whether Windows' global 1 ms timer is currently active (for diagnostics and tests).</summary>
    internal static bool IsTimerSharp
    {
        get
        {
            lock (timerLock)
                return timerSharp;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    private static extern int SetWaitableTimer(nint timer, ref long due, int period, nint routine, nint argument, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    /// <summary>
    /// Has the system wake sleeping threads to the millisecond for as long as anybody who keeps time runs. Windows
    /// otherwise only looks every 15 or so, which is no way to run a railway.
    /// </summary>
    public static void SharpenTimer(bool on)
    {
        if (!OperatingSystem.IsWindows()) return;

        lock (timerLock)
        {
            timerUsers += on ? 1 : -1;
            ApplyTimer();
        }
    }

    /// <summary>
    /// Lets the system's timer go back to its usual pace whoever keeps time, for a program asleep (WindowManager.Sleep),
    /// where nothing keeps time and the sharp timer only has every wait with a timeout in the process come round sooner.
    /// Until it's said again with false.
    /// </summary>
    public static void RestTimer(bool resting)
    {
        if (!OperatingSystem.IsWindows()) return;

        lock (timerLock)
        {
            timerResting = resting;
            ApplyTimer();
        }
    }

    private static readonly Lock timerLock = new();
    private static bool timerResting, timerSharp;

    // Helper method to have the system's timer as sharp as asked, under the lock
    private static void ApplyTimer()
    {
        bool sharp = timerUsers > 0 && !timerResting;
        if (sharp == timerSharp)
            return;

        try
        {
            if (sharp) timeBeginPeriod(1);
            else timeEndPeriod(1);
            timerSharp = sharp;
        }
        catch (Exception)
        {
            // Without it time is still kept, by spinning for more of the wait
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);
}
