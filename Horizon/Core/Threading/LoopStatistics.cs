namespace Horizon.Core.Threading;

/// <summary>
/// How one of the things an engine does over and over has been doing (drawing, updating the state, stepping the
/// physics): how often it really comes round, how long its turns take and how unevenly they come. Written by
/// whoever does it and read by whoever wants to show it (the debugger), from any thread.
/// </summary>
public sealed class LoopStatistics
{
    /// <summary>How many turns the history goes back.</summary>
    public const int HISTORY = 240;

    /// <summary>What the thread of a loop is doing right now, see <see cref="State"/>.</summary>
    public enum LoopState
    {
        /// <summary>Not started, or done for good.</summary>
        Stopped,

        /// <summary>Sleeping until its next turn is due.</summary>
        Sleeping,

        /// <summary>Held up by another thread, for what they share (the render thread setting a scene up, say).</summary>
        Blocked,

        /// <summary>Taking a turn.</summary>
        Working,
    }

    private volatile LoopState state;

    /// <summary>What the thread of the loop is doing right now, as of whoever runs it last said.</summary>
    public LoopState State => state;

    /// <summary>Says what the thread is doing now. Written by the loop itself at every change.</summary>
    public void SetState(LoopState now) => state = now;

    // How much of a new measurement the running averages take over
    private const double SMOOTHING = 0.05;

    private readonly Lock gate = new();
    private readonly float[] work = new float[HISTORY];
    private int cursor;

    public string Name { get; }

    /// <summary>How many turns a second the loop is meant to take, 0 for one that takes them as they come (rendering without a limit).</summary>
    public double TargetRate { get; }

    /// <summary>How many turns a second it has been taking.</summary>
    public double Rate { get; private set; }

    /// <summary>How long a turn takes, in milliseconds: on average and the longest of the history.</summary>
    public double WorkMs { get; private set; }
    public double PeakWorkMs { get; private set; }

    /// <summary>How long a turn waits for another loop to be done with what they share, in milliseconds on average.</summary>
    public double WaitMs { get; private set; }

    /// <summary>How unevenly the turns come: how far the time between two of them is off what it has been on average, in milliseconds.</summary>
    public double JitterMs { get; private set; }

    /// <summary>How much of the time it has the loop spends working, from 0 to 1 (and over, for one that can't keep up).</summary>
    public double Load => Rate > 0.0 ? WorkMs * 0.001 * Rate : 0.0;

    /// <summary>How many turns came late enough that another was due already, and how many of those were let go for good.</summary>
    public long LateTurns { get; private set; }
    public long DroppedTurns { get; private set; }

    /// <summary>
    /// How many bytes a turn allocates, averaged the way the rest is. Whatever is allocated every turn is what the
    /// garbage collector has to stop the game for sooner or later, so the nearer this is to nothing the better.
    /// </summary>
    public double AllocatedPerTurn { get; private set; }

    /// <summary>
    /// How many bytes the loop allocates a second at the rate it is going.
    /// </summary>
    public double AllocatedPerSecond => AllocatedPerTurn * Rate;

    private double period;

    public LoopStatistics(string name, double targetRate)
    {
        Name = name;
        TargetRate = targetRate;
        period = targetRate > 0.0 ? 1.0 / targetRate : 0.0;
    }

    /// <summary>
    /// Notes a turn of the loop.
    /// </summary>
    /// <param name="workSeconds">How long the work of it took.</param>
    /// <param name="waitSeconds">How long it waited for something another loop was holding.</param>
    /// <param name="sinceLast">How long it has been since the turn before.</param>
    /// <param name="turns">How many turns this really was, for a loop that makes up for the ones it missed.</param>
    /// <param name="allocatedBytes">How much the thread of the loop allocated while it worked, see <see cref="GC.GetAllocatedBytesForCurrentThread"/>.</param>
    public void Record(double workSeconds, double waitSeconds, double sinceLast, int turns = 1, long allocatedBytes = 0)
    {
        lock (gate)
        {
            AllocatedPerTurn += (allocatedBytes / (double)Math.Max(1, turns) - AllocatedPerTurn) * SMOOTHING;

            double workMs = workSeconds * 1000.0 / Math.Max(1, turns);

            period = period <= 0.0 ? sinceLast : period + (sinceLast - period) * SMOOTHING;
            WorkMs += (workMs - WorkMs) * SMOOTHING;
            WaitMs += (waitSeconds * 1000.0 - WaitMs) * SMOOTHING;
            JitterMs += (Math.Abs(sinceLast - period) * 1000.0 - JitterMs) * SMOOTHING;
            Rate = period > 0.0 ? turns / period : 0.0;

            if (turns > 1) LateTurns += turns - 1;

            work[cursor] = (float)workMs;
            cursor = (cursor + 1) % HISTORY;

            float peak = 0.0f;
            foreach (float value in work) peak = MathF.Max(peak, value);
            PeakWorkMs = peak;
        }
    }

    internal void NoteDropped(int turns)
    {
        lock (gate) DroppedTurns += turns;
    }

    /// <summary>
    /// How uneven the last turns have been, out of the history. The 99th percentile of how long a turn took, how long the
    /// slowest one in every hundred takes on average (what "1% low" frame rates are made of), and how many turns took
    /// more than twice the budget (the rate the loop is meant to run at, or sixty a second for one with no limit), which
    /// is a turn anybody would notice.
    /// </summary>
    public void Instability(out double percentile99Ms, out double worstPercentMs, out int stutters)
    {
        Span<float> sorted = stackalloc float[HISTORY];
        int count = 0;
        double budget = 1000.0 / (TargetRate > 0.0 ? TargetRate : 60.0);
        stutters = 0;

        lock (gate)
        {
            foreach (float value in work)
            {
                if (value <= 0.0f) continue;
                sorted[count++] = value;
                if (value > budget * 2.0) stutters++;
            }
        }

        if (count == 0)
        {
            percentile99Ms = worstPercentMs = 0.0;
            return;
        }

        sorted = sorted[..count];
        sorted.Sort();
        percentile99Ms = sorted[Math.Min(count - 1, (int)(count * 0.99))];

        int worst = Math.Max(1, count / 100);
        double sum = 0.0;
        for (int i = count - worst; i < count; i++) sum += sorted[i];
        worstPercentMs = sum / worst;
    }

    /// <summary>Copies how long the last turns took (in milliseconds, oldest first) into a buffer of <see cref="HISTORY"/> values.</summary>
    public void CopyHistory(Span<float> into)
    {
        lock (gate)
        {
            for (int i = 0; i < Math.Min(into.Length, HISTORY); i++)
            {
                into[i] = work[(cursor + i) % HISTORY];
            }
        }
    }
}
