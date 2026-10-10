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

        while (true)
        {
            double left = moment - Stopwatch.GetTimestamp() / frequency;
            if (left <= 0.0) return;

            if (left > SPIN_MARGIN) Thread.Sleep(1);
            else Thread.SpinWait(32);
        }
    }

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
