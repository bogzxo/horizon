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

    private static string? PathNow
    {
        get
        {
            if (tracePath is not null)
                return tracePath;

            long now = Environment.TickCount64;
            if (now - switchLooked > 1000)
            {
                switchLooked = now;
                switchedOn = !OperatingSystem.IsWindows() && File.Exists(SWITCH);
            }

            return switchedOn ? SWITCHED : null;
        }
    }

    /// <summary>Writes a line of the trace, nothing without one.</summary>
    public static void Trace(string line)
    {
        if (PathNow is not { } path)
            return;

        try
        {
            File.AppendAllText(path, FormattableString.Invariant($"{System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3} ui {line}\n"));
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
