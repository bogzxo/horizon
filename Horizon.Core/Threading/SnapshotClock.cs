using System.Diagnostics;

namespace Horizon.Core.Threading;

/// <summary>
/// Hands what the simulation looked like at the end of each of its ticks over to whoever draws it, without either of
/// them ever waiting for the other. This is the double (well, quadruple) buffer at the heart of the engine.
/// <para>
/// There are four slots. At the end of a tick the simulation captures everything that is drawn into a slot nobody is
/// looking at (<see cref="BeginCapture"/>, then every <see cref="Snapshot{T}"/> is published, then
/// <see cref="EndCapture"/>), which makes it the newest. At the start of a frame the renderer takes the two ticks the
/// moment it shows lies between (<see cref="Acquire"/>) and has them to itself until it lets go (<see cref="Release"/>):
/// nothing is ever written to a slot that is being drawn from. That pair is the newest tick and the one before it, or
/// the one before that and the one before that again; with the renderer holding two of them the simulation always has
/// another one to write to. A renderer that is slower than the simulation simply never sees some ticks, the one written
/// over is one it never asked for, and the next pair it gets spans two ticks instead of one, which interpolates just as well.
/// </para>
/// <para>
/// The renderer doesn't draw the newest tick as it is. It draws the moment a little in the past that lies between the
/// two it holds (see <see cref="RenderFrame.Alpha"/>), so whatever moves goes the same distance for the same time from
/// frame to frame, however the ticks and the frames line up. How far in the past is worked out from how far apart the
/// ticks come and how late they are published, which keeps it as short as it can be.
/// </para>
/// <para>
/// Why three ticks to pick a pair from and not just the newest two: ticks are never published equally late. Drawn
/// between the newest two, a moment far enough back for the next tick to always be there in time is, just after a tick
/// that came a bit early, before the older of the two. Frames then jump to that tick and stand still until real time
/// catches up, every single tick. At 60 frames a second that's a sliver of a frame nobody sees; with thousands of frames
/// a second it's a proper stutter. With the tick before that to fall back on there is a whole tick of room for that.
/// </para>
/// The lock in here is only ever held for a handful of instructions, by one side at a time.
/// </summary>
public sealed class SnapshotClock
{
    /// <summary>
    /// How many slots there are: the newest three ticks for the renderer to draw a pair of, and one for the simulation
    /// to write to.
    /// </summary>
    public const int SLOTS = 4;

    // Added to how far in the past frames are drawn, for the ticks that come a little later than the ones before them
    private const double MARGIN = 0.0005;

    // How much of a new measurement of the tick interval is taken over, and of the lag when it's lower than it was. A
    // tick published later than the lag goes straight up to it: frames that catch up with the newest tick hold still,
    // which is a lot worse than being a millisecond or two further behind. It comes back down over a second or so
    private const double INTERVAL_SMOOTHING = 0.05;
    private const double LAG_FALL = 0.005;

    // The most the lag is taken to be, as a share of a tick. There's a tick of room between the newest snapshot and the
    // one two before it to draw from; a tick later than that is a proper hitch whatever is done about it
    private const double MOST_LAG = 0.75;

    // How hard the moment frames show is pulled towards where the delay says it ought to be, a second: hard when it has
    // to go further back (or a late tick leaves frames with nothing newer to show), gently when it can come forward.
    // And the most it is ever pulled, as a share of the real time that went by, so whatever moves never speeds up or
    // slows down by more than that. Past SNAP_TICKS ticks out (a stall, a scene loading) it just goes there
    private const double PULL_BACK = 8.0, PULL_FORWARD = 1.0;
    private const double MAX_PULL = 0.05;
    private const double SNAP_TICKS = 4.0;

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

    // The moment (in seconds of real time) the last interpolated frame showed. Goes forward at the pace of real time
    // rather than jumping about with every new guess at the delay, see Shown
    private double shownAt = double.NaN;

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
            float alpha = 1.0f;

            if (mode != PresentationMode.Interpolated)
                shownAt = double.NaN;
            else
            {
                Shown(now / frequency, realDelta);

                // Past the newest tick there's nothing to show, it's late: wait for it rather than carry on without it
                // and jump to where the moment got to when it comes. Being that much further behind is eased back out
                double newest = stamps[current] / frequency;
                if (shownAt > newest)
                    shownAt = newest;

                // The newest whole snapshot before the newest one, and the one before that
                previous = Before(current);
                int older = previous == current ? current : Before(previous);

                // A tick that came early leaves the moment that's shown before the older of the newest two, it's
                // between the two before them then
                if (older != previous && shownAt < stamps[previous] / frequency)
                    (previous, current) = (older, previous);

                if (previous != current && stamps[current] > stamps[previous])
                {
                    double from = stamps[previous] / frequency, to = stamps[current] / frequency;
                    alpha = (float)Math.Clamp((shownAt - from) / (to - from), 0.0, 1.0);
                }
            }

            pinnedPrevious = previous;
            pinnedCurrent = current;

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
    /// Helper method to find the newest whole snapshot published before the one in a slot, that slot again if there is none.
    /// </summary>
    private int Before(int slot)
    {
        int found = slot;
        for (int i = 0; i < SLOTS; i++)
        {
            if (i == slot || sequences[i] == 0 || sequences[i] >= sequences[slot])
                continue;

            if (found == slot || sequences[i] > sequences[found])
                found = i;
        }

        return found;
    }

    /// <summary>
    /// Helper method to move the moment frames show on, by as much real time as went by since the last one, pulled a
    /// little towards <c>now - Delay</c>. The delay is a guess made again on every tick (how far apart they come, how late
    /// they're published) and it wobbles by a fair few microseconds every time. Shown as it is, that wobble lands on the
    /// next frame in full: nothing at 60 frames a second, a stutter of a good share of the frame when there are thousands
    /// of them.
    /// </summary>
    private void Shown(double now, float realDelta)
    {
        double target = now - DelayLocked();
        double predicted = shownAt + realDelta;
        double off = target - predicted;

        if (double.IsNaN(shownAt) || realDelta <= 0.0f || Math.Abs(off) > SNAP_TICKS * Math.Max(interval, 1.0 / 240.0))
        {
            shownAt = target;
            return;
        }

        double pull = off * Math.Min(1.0, (off < 0.0 ? PULL_BACK : PULL_FORWARD) * realDelta);
        double most = MAX_PULL * realDelta;
        shownAt = predicted + Math.Clamp(pull, -most, most);
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

        throw new UnreachableException("Four slots and at most two of them held, one has to be free.");
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
            late = Math.Min(late, interval * MOST_LAG);

        lag = late > lag ? late : lag + (late - lag) * LAG_FALL;
    }
}
