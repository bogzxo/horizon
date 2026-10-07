using System.Diagnostics;

namespace Horizon.Core.Threading;

/// <summary>
/// Hands what the simulation looked like at the end of each of its ticks over to whoever draws it, without either of
/// them ever waiting for the other. This is the double (in fact triple) buffer at the heart of the engine.
/// <para>
/// There are three slots. At the end of a tick the simulation captures everything that is drawn into a slot nobody is
/// looking at (<see cref="BeginCapture"/>, then every <see cref="Snapshot{T}"/> is published, then
/// <see cref="EndCapture"/>), which makes it the newest. At the start of a frame the renderer takes the newest slot and
/// the one before it (<see cref="Acquire"/>) and has them to itself until it lets go (<see cref="Release"/>): nothing
/// is ever written to a slot that is being drawn from. With the renderer holding two slots the simulation always has
/// the third to write to. A renderer that is slower than the simulation simply never sees some ticks, the one written
/// over is one it never asked for, and the next pair it gets spans two ticks instead of one, which interpolates just as well.
/// </para>
/// <para>
/// The renderer doesn't draw the newest tick as it is. It draws the moment a little in the past that lies between the
/// two it holds (see <see cref="RenderFrame.Alpha"/>), so whatever moves goes the same distance for the same time from
/// frame to frame, however the ticks and the frames line up. How far in the past is worked out from how far apart the
/// ticks come and how late they are published, which keeps it as short as it can be.
/// </para>
/// The lock in here is only ever held for a handful of instructions, by one side at a time.
/// </summary>
public sealed class SnapshotClock
{
    /// <summary>How many slots there are: two for the renderer and one for the simulation to write to.</summary>
    public const int SLOTS = 3;

    // Added to how far in the past frames are drawn, for the ticks that come a little later than the ones before them
    private const double MARGIN = 0.0005;

    // How much of a new measurement of the tick interval and of the lag is taken over. The lag goes up a lot faster than
    // it comes down: a frame that catches up with the newest tick holds still, which is worse than being a bit late
    private const double INTERVAL_SMOOTHING = 0.05;
    private const double LAG_RISE = 0.25, LAG_FALL = 0.02;

    private readonly Lock sync = new();

    // Which publish every slot holds (0 for none, or one that is being written), the moment it stands for (stopwatch
    // ticks) and the simulated time at that moment (seconds)
    private readonly long[] sequences = new long[SLOTS];
    private readonly long[] stamps = new long[SLOTS];
    private readonly double[] times = new double[SLOTS];

    private int latest = -1, capturing = -1;
    private long captureSequence, nextSequence = 1;
    private int pinnedPrevious = -1, pinnedCurrent = -1;

    // How far apart publishes come and how long after the moment they stand for, in seconds
    private double interval, lag;
    private long lastStamp;

    // The renderer's side: when the last frame was, and the time it showed
    private long lastFrame;
    private double lastPresentation = double.NaN;

    /// <summary>
    /// The clock of the running engine, which <see cref="Snapshot{T}"/>s publish to unless they are given another.
    /// </summary>
    public static SnapshotClock? Active { get; set; }

    /// <summary>The slot a capture is being written to, -1 while no capture is going on. Simulation thread.</summary>
    public int CaptureSlot => capturing;

    /// <summary>Which publish the capture going on is going to be. Simulation thread.</summary>
    public long CaptureSequence => captureSequence;

    /// <summary>How many publishes there have been.</summary>
    public long Published
    {
        get
        {
            lock (sync) return latest < 0 ? 0 : sequences[latest];
        }
    }

    /// <summary>How far apart (in seconds) the publishes have been coming, smoothed. 0 before the second one.</summary>
    public double TickInterval
    {
        get
        {
            lock (sync) return interval;
        }
    }

    /// <summary>How far in the past (in seconds) frames are drawn when they are interpolated.</summary>
    public double Delay
    {
        get
        {
            lock (sync) return DelayLocked();
        }
    }

    private double DelayLocked() => interval + lag + MARGIN;

    /// <summary>
    /// Starts a capture into a slot the renderer isn't holding. Every <see cref="Snapshot{T}"/> that is published until
    /// <see cref="EndCapture"/> is part of it. Simulation thread.
    /// </summary>
    public void BeginCapture()
    {
        lock (sync)
        {
            if (capturing >= 0)
                throw new InvalidOperationException("A capture is going on already, it has to be ended before the next one begins.");

            int slot = PickFree();

            capturing = slot;
            captureSequence = nextSequence++;

            // Not a whole snapshot any more from here on, so nobody is handed it
            sequences[slot] = 0;
            if (latest == slot)
                latest = NewestComplete(slot);
        }
    }

    /// <summary>
    /// Finishes the capture that was begun, which makes it the newest snapshot. Simulation thread.
    /// </summary>
    /// <param name="stamp">The moment (in stopwatch ticks) the snapshot shows the simulation at: when its tick was due.</param>
    /// <param name="simulatedTime">How long (in seconds) the simulation had been running for at that moment.</param>
    /// <param name="publishedAt">When this happens (stopwatch ticks), now unless said otherwise.</param>
    public void EndCapture(long stamp, double simulatedTime, long publishedAt = 0)
    {
        if (publishedAt == 0)
            publishedAt = Stopwatch.GetTimestamp();

        lock (sync)
        {
            if (capturing < 0)
                throw new InvalidOperationException("There is no capture to end.");

            int slot = capturing;
            sequences[slot] = captureSequence;
            stamps[slot] = stamp;
            times[slot] = simulatedTime;

            latest = slot;
            capturing = -1;

            Measure(stamp, publishedAt);
        }
    }

    /// <summary>
    /// Takes the snapshots a frame is to be drawn from and holds on to them until <see cref="Release"/>. Render thread.
    /// </summary>
    /// <param name="now">When the frame is (stopwatch ticks), now unless said otherwise.</param>
    /// <returns>The frame, without a snapshot (see <see cref="RenderFrame.HasSnapshot"/>) before the first publish.</returns>
    public RenderFrame Acquire(PresentationMode mode, long now = 0)
    {
        if (now == 0)
            now = Stopwatch.GetTimestamp();

        double frequency = Stopwatch.Frequency;

        lock (sync)
        {
            float realDelta = lastFrame == 0 ? 0.0f : (float)((now - lastFrame) / frequency);
            lastFrame = now;

            if (latest < 0)
            {
                pinnedPrevious = pinnedCurrent = -1;
                return new RenderFrame(this, -1, -1, 0, 0, 1.0f, 0.0, 0.0f, realDelta, mode);
            }

            int current = latest;
            int previous = current;

            if (mode == PresentationMode.Interpolated)
            {
                // The newest whole snapshot before the newest one
                for (int i = 0; i < SLOTS; i++)
                {
                    if (i == current || sequences[i] == 0 || sequences[i] >= sequences[current])
                        continue;

                    if (previous == current || sequences[i] > sequences[previous])
                        previous = i;
                }
            }

            pinnedPrevious = previous;
            pinnedCurrent = current;

            float alpha = 1.0f;
            if (previous != current && stamps[current] > stamps[previous])
            {
                double shown = now / frequency - DelayLocked();
                double from = stamps[previous] / frequency, to = stamps[current] / frequency;
                alpha = (float)Math.Clamp((shown - from) / (to - from), 0.0, 1.0);
            }

            // The time things that only exist on the renderer's side go by (particles on the GPU, a flash fading). It
            // only ever goes forward, even when the pair that is drawn from moves on a little early
            double presentation = times[previous] + (times[current] - times[previous]) * alpha;
            if (!double.IsNaN(lastPresentation) && presentation < lastPresentation)
                presentation = lastPresentation;

            float simDelta = double.IsNaN(lastPresentation) ? 0.0f : (float)(presentation - lastPresentation);
            lastPresentation = presentation;

            return new RenderFrame(this, previous, current, sequences[previous], sequences[current], alpha, presentation, simDelta, realDelta, mode);
        }
    }

    /// <summary>
    /// Lets go of the snapshots the last <see cref="Acquire"/> took, for the simulation to write to again. Render thread.
    /// </summary>
    public void Release()
    {
        lock (sync)
        {
            pinnedPrevious = pinnedCurrent = -1;
        }
    }

    /// <summary>The publish a slot holds, 0 for none (or one that is being written).</summary>
    internal long SequenceOf(int slot)
    {
        lock (sync) return sequences[slot];
    }

    /// <summary>
    /// Helper method to find the slot to capture into: one the renderer isn't holding and that isn't the newest, the
    /// oldest of those. If the renderer holds the other two the newest is all there is, and it is written over.
    /// </summary>
    private int PickFree()
    {
        int best = -1;
        for (int i = 0; i < SLOTS; i++)
        {
            if (i == pinnedPrevious || i == pinnedCurrent || i == latest)
                continue;

            if (best < 0 || sequences[i] < sequences[best])
                best = i;
        }

        if (best >= 0)
            return best;

        for (int i = 0; i < SLOTS; i++)
        {
            if (i != pinnedPrevious && i != pinnedCurrent)
                return i;
        }

        throw new UnreachableException("Three slots and at most two of them held, one has to be free.");
    }

    /// <summary>
    /// Helper method to find the newest whole snapshot that isn't in a slot, -1 if there is none.
    /// </summary>
    private int NewestComplete(int except)
    {
        int newest = -1;
        for (int i = 0; i < SLOTS; i++)
        {
            if (i == except || sequences[i] == 0)
                continue;

            if (newest < 0 || sequences[i] > sequences[newest])
                newest = i;
        }

        return newest;
    }

    /// <summary>
    /// Helper method to keep track of how far apart the ticks come and how late they are published, which is how far
    /// in the past frames have to be drawn for there always to be a tick on either side of the moment they show.
    /// </summary>
    private void Measure(long stamp, long publishedAt)
    {
        double frequency = Stopwatch.Frequency;

        if (lastStamp != 0 && stamp > lastStamp)
        {
            double gap = (stamp - lastStamp) / frequency;

            // A stall (a scene being loaded) is no tick interval, it would have everything drawn way in the past for a while
            if (interval > 0.0)
                gap = Math.Min(gap, interval * 4.0);

            interval = interval == 0.0 ? gap : interval + (gap - interval) * INTERVAL_SMOOTHING;
        }

        lastStamp = stamp;

        double late = Math.Max(0.0, (publishedAt - stamp) / frequency);
        if (interval > 0.0)
            late = Math.Min(late, interval * 4.0);

        lag += (late - lag) * (late > lag ? LAG_RISE : LAG_FALL);
    }
}
