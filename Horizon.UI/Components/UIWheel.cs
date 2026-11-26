using System.Diagnostics;
using System.Text;

namespace Horizon.UI.Components;

/// <summary>
/// What kind of wheel turn is being handed out, for the <c>OnScroll</c> that is running. A notched wheel's turn is whole
/// notches that scrolling components step by (and ease to). A trackpad's, or the momentum after it, is fractional notches
/// (one is <see cref="NotchUnits"/> layout units) that should move the content by exactly that much, with nothing between
/// the finger and the pixels. Set by the compositor around the call, on the UI thread.
/// </summary>
internal static class UIWheel
{
    /// <summary>Layout units a notch is worth, see <c>PreciseScroll.NotchPoints</c>.</summary>
    public const float NotchUnits = 48.0f;

    /// <summary>Whether the delta being handed out is a continuous one.</summary>
    [ThreadStatic] public static bool Precise;

    /// <summary>
    /// Whether a trackpad's fingers are on it, scrolling, this tick: what's pulled past an end is held there with the give of
    /// a rubber band, and springs back once they lift. Only a Mac says, elsewhere it's never held. For the whole tick.
    /// </summary>
    [ThreadStatic] public static bool Held;

    /// <summary>Whether this tick's turn is the glide after a flick, which bounces off an end rather than stretching it.</summary>
    [ThreadStatic] public static bool Gliding;

    /// <summary>How long this tick is, seconds, to tell how fast a glide hit an end.</summary>
    [ThreadStatic] public static float Seconds;

    /// <summary>Where each tick's turn is written when HORIZON_SCROLL_TRACE names a file, beside what the platform said of it.</summary>
    private static readonly string? tracePath = Environment.GetEnvironmentVariable("HORIZON_SCROLL_TRACE") is { Length: > 0 } path ? path : null;

    // Or switched on in a running program by a file being there, looked for once a second: /tmp/horizon-scroll-trace.on
    // writes to /tmp/horizon-scroll-trace.txt, for a bug that only shows in a session that's been going a while
    private const string SWITCH = "/tmp/horizon-scroll-trace.on", SWITCHED = "/tmp/horizon-scroll-trace.txt";
    private static long switchLooked;
    private static bool switchedOn;

    // Buffered trace state: lazily allocated only when tracing is actually active so unset tracing has zero overhead.
    internal const int TraceFlushThresholdChars = 8192;
    internal const int TraceIdleFlushMs = 250;

    private static readonly object traceLock = new();
    private static StringBuilder? traceBuffer;
    private static string? bufferedPath;
    private static Timer? idleFlushTimer;
    private static bool exitHooked;
    private static string? tracePathOverride;

    private static string? PathNow
    {
        get
        {
            if (tracePathOverride is { Length: > 0 } overridden)
                return overridden;
            if (tracePath is not null)
                return tracePath;
            if (OperatingSystem.IsWindows())
                return null;

            long now = Environment.TickCount64;
            if (now - switchLooked > 1000)
            {
                switchLooked = now;
                switchedOn = File.Exists(SWITCH);
            }

            return switchedOn ? SWITCHED : null;
        }
    }

    /// <summary>Buffers a line of the trace and flushes when the buffer fills, after idle, or on shutdown; nothing when unset.</summary>
    public static void Trace(string line)
    {
        if (PathNow is not { } path)
            return;

        AppendTraceLine(path, line);
    }

    /// <summary>Flushes any buffered scroll trace lines to disk immediately.</summary>
    internal static void FlushTrace()
    {
        lock (traceLock)
        {
            idleFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            FlushLocked();
        }
    }

    /// <summary>Test hook to redirect scroll trace output without mutating process-wide environment variables.</summary>
    internal static void SetTracePathOverride(string? path)
    {
        FlushTrace();
        tracePathOverride = path is { Length: > 0 } ? path : null;
    }

    private static void AppendTraceLine(string path, string line)
    {
        string formatted = FormattableString.Invariant($"{Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency:F3} ui {line}\n");
        lock (traceLock)
        {
            if (!exitHooked)
            {
                exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => FlushTrace();
            }

            if (bufferedPath is not null && !string.Equals(bufferedPath, path, StringComparison.Ordinal))
                FlushLocked();

            bufferedPath = path;
            traceBuffer ??= new StringBuilder(TraceFlushThresholdChars);
            traceBuffer.Append(formatted);

            if (traceBuffer.Length >= TraceFlushThresholdChars)
            {
                idleFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                FlushLocked();
            }
            else
            {
                idleFlushTimer ??= new Timer(static _ => FlushTrace(), null, Timeout.Infinite, Timeout.Infinite);
                idleFlushTimer.Change(TraceIdleFlushMs, Timeout.Infinite);
            }
        }
    }

    private static void FlushLocked()
    {
        if (traceBuffer is null || traceBuffer.Length == 0 || bufferedPath is null)
            return;

        string payload = traceBuffer.ToString();
        string path = bufferedPath;
        traceBuffer.Clear();

        try
        {
            File.AppendAllText(path, payload);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Whether there's a trace to write to.</summary>
    public static bool Tracing => PathNow is not null;

    /// <summary>Layout units a delta is worth: <paramref name="notchUnits"/> for a notch of a notched wheel, the exact points for a precise one.</summary>
    public static float Units(float delta, float notchUnits) => Precise ? delta * NotchUnits : delta * notchUnits;
}
