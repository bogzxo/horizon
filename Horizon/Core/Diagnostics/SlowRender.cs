using System.Diagnostics;

using Horizon.Logging;

namespace Horizon.Core.Diagnostics;

/// <summary>
/// Says which part of a frame was slow, for finding a hitch. Start the game with <c>HORIZON_LOG_SLOW=20</c> and every
/// entity, component and pass of the lighting whose drawing took 20 ms or more is logged with how long, nested ones as
/// well, so the log reads from the inside out down to whatever the time went on. What is set up (Initialize, a scene
/// being set up behind a transition) is timed the same way. Off (the usual) it costs a branch.
/// Render thread.
/// <code>
/// long started = SlowRender.Start();
/// DoTheThing();
/// SlowRender.Stop(started, "the thing");
/// </code>
/// </summary>
public static class SlowRender
{
    public const string ENVIRONMENT_VARIABLE = "HORIZON_LOG_SLOW";

    /// <summary>How long (in milliseconds) a part has to take to be logged, 0 for never.</summary>
    public static double Threshold { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable(ENVIRONMENT_VARIABLE), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms) ? ms : 0.0;

    public static bool On => Threshold > 0.0;

    /// <summary>When a part started, 0 while it is off.</summary>
    public static long Start() => On ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Logs a part that took longer than the threshold. What it is only becomes text when it was slow.</summary>
    /// <param name="doing">What it took that long to do, drawing unless said, setting up for an Initialize.</param>
    public static void Stop(long started, object what, string doing = "draw")
    {
        if (started == 0) return;

        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (ms < Threshold) return;

        string name = what switch
        {
            string text => text,
            Entity { Name.Length: > 0 } entity => $"{entity.GetType().Name} '{entity.Name}'",
            _ => what.GetType().Name
        };
        Log.Info($"[Slow] {name} took {ms:0.0} ms to {doing}.");
    }
}
