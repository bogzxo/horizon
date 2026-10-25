using System.Diagnostics;

using Horizon.Logging;

namespace Horizon.Core.Diagnostics;

/// <summary>
/// Says how long after the process started each step of getting going was done, for finding out where the start of a
/// program goes. Start it with <c>HORIZON_LOG_STARTUP=1</c> and every <see cref="Mark"/> is written to the log with the
/// milliseconds since the process was made, which is what somebody double clicking it waits through. Off (the usual)
/// it costs a branch.
/// </summary>
public static class StartupTrace
{
    public const string ENVIRONMENT_VARIABLE = "HORIZON_LOG_STARTUP";

    public static bool On { get; } = Environment.GetEnvironmentVariable(ENVIRONMENT_VARIABLE) is { Length: > 0 } asked && asked != "0";

    // When the process was made, against the stopwatch, worked out once from the system's start time
    private static readonly long processStarted = FindProcessStart();

    /// <summary>Set once the first scene is set up, the frame after that is the one the start ends with.</summary>
    public static volatile bool SceneReady;

    // Set once the first frame of the first scene went out, after that nothing is marked
    private static int done;

    /// <summary>Logs a step with the time since the process started. From any thread.</summary>
    public static void Mark(string step)
    {
        if (!On || Volatile.Read(ref done) != 0) return;

        Log.Info($"[Startup] {step} at {Since():0} ms{Weight()}.");
    }

    /// <summary>The last step, the first frame of a scene shown, which is where the start of a program ends.</summary>
    public static void Finish(string step)
    {
        if (!On || Interlocked.Exchange(ref done, 1) != 0) return;

        Log.Info($"[Startup] {step} at {Since():0} ms{Weight()}.");
    }

    private static double Since() => Stopwatch.GetElapsedTime(processStarted).TotalMilliseconds;

    // With HORIZON_LOG_STARTUP=memory what the process weighs at every step as well, which takes a moment to ask
    private static readonly bool weighs = Environment.GetEnvironmentVariable(ENVIRONMENT_VARIABLE) == "memory";

    private static string Weight()
    {
        if (!weighs) return string.Empty;

        using var process = Process.GetCurrentProcess();
        return $", working set {process.WorkingSet64 / (1024 * 1024)} MB, private {process.PrivateMemorySize64 / (1024 * 1024)} MB, heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB";
    }

    private static long FindProcessStart()
    {
        if (!On) return 0;

        try
        {
            using var process = Process.GetCurrentProcess();
            double ago = (DateTime.Now - process.StartTime).TotalSeconds;
            return Stopwatch.GetTimestamp() - (long)(ago * Stopwatch.Frequency);
        }
        catch (Exception)
        {
            // No start time to be had, it counts from the first mark then
            return Stopwatch.GetTimestamp();
        }
    }
}
