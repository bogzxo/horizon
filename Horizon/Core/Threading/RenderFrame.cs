namespace Horizon.Core.Threading;

/// <summary>
/// How frames show the simulation.
/// </summary>
public enum PresentationMode : byte
{
    /// <summary>
    /// Every frame shows the moment between the last two ticks it is due to, so whatever moves goes the same distance
    /// for the same time from frame to frame at any frame rate. What is on screen is up to a tick behind the simulation.
    /// </summary>
    Interpolated,

    /// <summary>
    /// Every frame shows the newest tick as it is. As little delay as there can be, but whatever moves steps unevenly
    /// whenever the frames don't come at a whole multiple of the tick rate.
    /// </summary>
    Latest
}

/// <summary>
/// What a frame is drawn from, the two snapshots it holds (see <see cref="SnapshotClock"/>) and how far between them
/// the moment it shows lies. The frame that is being drawn is <see cref="Active"/>, on the render thread, which is how
/// everything that draws gets at it without it being handed down through every <c>Render</c>.
/// </summary>
/// <param name="Clock">The clock the snapshots are from.</param>
/// <param name="PreviousSlot">The slot of the older snapshot, the same as <paramref name="CurrentSlot"/> if there is only one.</param>
/// <param name="CurrentSlot">The slot of the newer snapshot, -1 if there is none yet.</param>
/// <param name="PreviousSequence">Which publish the older snapshot is.</param>
/// <param name="CurrentSequence">Which publish the newer snapshot is.</param>
/// <param name="Alpha">How far from the older snapshot to the newer one the moment the frame shows lies, from 0 to 1.</param>
/// <param name="PresentationTime">The simulated time (in seconds) at the moment the frame shows. Only ever goes forward.</param>
/// <param name="SimDelta">How much simulated time (in seconds) has gone by on screen since the last frame. Nothing while the simulation stands still.</param>
/// <param name="RealDelta">How much real time (in seconds) has gone by since the last frame.</param>
/// <param name="Mode">How the frame shows the simulation.</param>
public readonly record struct RenderFrame(
    SnapshotClock? Clock,
    int PreviousSlot,
    int CurrentSlot,
    long PreviousSequence,
    long CurrentSequence,
    float Alpha,
    double PresentationTime,
    float SimDelta,
    float RealDelta,
    PresentationMode Mode)
{
    [ThreadStatic]
    private static RenderFrame active;

    /// <summary>
    /// The frame that is being drawn, on the render thread. On any other thread, and on the render thread outside of
    /// a frame, it has no snapshot.
    /// </summary>
    public static RenderFrame Active => active;

    /// <summary>Makes a frame the one that is being drawn on this thread, until the next one.</summary>
    public static void Begin(in RenderFrame frame) => active = frame;

    /// <summary>Has this thread draw no frame any more.</summary>
    public static void End() => active = default;

    /// <summary>Whether there is a snapshot to draw at all.</summary>
    public bool HasSnapshot => Clock is not null && CurrentSlot >= 0;

    /// <summary>
    /// Whether what draws has to draw from snapshots. The frame has them, and the simulation is running alongside it
    /// so nothing that draws may read the game itself, only what was published. Otherwise (setting a scene up with the
    /// simulation standing still, before the first tick) the game itself is there to be read.
    /// </summary>
    public bool IsDecoupled => HasSnapshot;

    /// <summary>Whether the frame shows a moment between two snapshots rather than one of them as it is.</summary>
    public bool Interpolating => HasSnapshot && PreviousSlot != CurrentSlot && Alpha < 1.0f;
}
