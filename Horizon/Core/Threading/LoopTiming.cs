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

    private static int timerUsers;

    /// <summary>
    /// Waits for a moment (in seconds of the stopwatch), asleep for most of it, awake for the end.
    /// </summary>
    public static void WaitUntil(double moment)
    {
        double frequency = Stopwatch.Frequency;

        // On Windows a timer that wakes to a fraction of a millisecond does most of the wait, so only the last
        // little bit is spun. Sleeping a millisecond at a time left up to two of them to spin, every tick and every
        // frame, which was most of what an editor sitting there blinking its caret cost
        if (PreciseTimer() is var timer && timer != 0)
        {
            double sleepFor = moment - Stopwatch.GetTimestamp() / frequency - PRECISE_MARGIN;
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

            if (left > SPIN_MARGIN) Thread.Sleep(1);
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

        try
        {
            if (on && Interlocked.Increment(ref timerUsers) == 1) timeBeginPeriod(1);
            if (!on && Interlocked.Decrement(ref timerUsers) == 0) timeEndPeriod(1);
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
