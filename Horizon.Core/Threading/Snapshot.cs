namespace Horizon.Core.Threading;

/// <summary>
/// Something that can be shown partway between two of its snapshots.
/// </summary>
public interface IBlendable<T> where T : struct
{
    /// <summary>
    /// What lies <paramref name="amount"/> of the way (0 to 1) from one snapshot to the next. What moves smoothly
    /// (positions, sizes, colours) is mixed, what changes in steps (a frame of an animation, a flag) is taken from
    /// <paramref name="from"/> until <paramref name="amount"/> is all the way, the way it was at the moment that is shown.
    /// </summary>
    static abstract T Blend(in T from, in T to, float amount);
}

/// <summary>
/// The state of one thing as the renderer is to see it, kept for each slot of the <see cref="SnapshotClock"/>.
/// The simulation <see cref="Publish"/>es what it is at the end of every tick (in its <c>Capture</c>), the renderer
/// reads the two snapshots of its frame (<see cref="TryGet(in RenderFrame, out T, out T, out bool)"/>) and never sees
/// the thing itself, which the simulation is free to go on changing in the meantime.
/// <code>
/// // Simulation thread, at the end of the tick
/// public override void Capture() => state.Publish(new State(Position, Tint));
///
/// // Render thread
/// if (state.TryBlend(RenderFrame.Active, out var shown)) Draw(shown);
/// </code>
/// Something that is put somewhere else rather than moved there (a respawn, a cut) calls <see cref="Break"/>, and is
/// not shown on its way from where it was to where it is.
/// </summary>
public sealed class Snapshot<T> where T : struct
{
    private readonly T[] values = new T[SnapshotClock.SLOTS];
    private readonly long[] written = new long[SnapshotClock.SLOTS];
    private readonly int[] epochs = new int[SnapshotClock.SLOTS];
    private int epoch;

    /// <summary>
    /// Has what is published next not be blended with what was published before it. Simulation thread.
    /// </summary>
    public void Break() => epoch++;

    /// <summary>
    /// Puts what the thing is now into the capture that is going on of the engine's clock. Simulation thread.
    /// </summary>
    /// <returns>False if no capture is going on, nothing was kept then.</returns>
    public bool Publish(in T value) => Publish(SnapshotClock.Active, value);

    /// <summary>
    /// Puts what the thing is now into the capture that is going on of a clock. Simulation thread.
    /// </summary>
    /// <returns>False if no capture is going on, nothing was kept then.</returns>
    public bool Publish(SnapshotClock? clock, in T value)
    {
        if (clock is null || clock.CaptureSlot is not (>= 0 and var slot))
            return false;

        values[slot] = value;
        written[slot] = clock.CaptureSequence;
        epochs[slot] = epoch;
        return true;
    }

    /// <summary>
    /// The newer of the snapshots of a frame. Render thread.
    /// </summary>
    /// <returns>False if the thing wasn't published in it: it didn't exist yet, or wasn't shown.</returns>
    public bool TryGet(in RenderFrame frame, out T current)
    {
        if (frame.HasSnapshot && written[frame.CurrentSlot] == frame.CurrentSequence)
        {
            current = values[frame.CurrentSlot];
            return true;
        }

        current = default;
        return false;
    }

    /// <summary>
    /// Both snapshots of a frame. Render thread.
    /// </summary>
    /// <param name="previous">The older one, the newer one again if the thing wasn't published in the older one (it is new).</param>
    /// <param name="continuous">Whether the two can be blended: both are there and nothing broke in between (see <see cref="Break"/>).</param>
    /// <returns>False if the thing wasn't published in the newer one.</returns>
    public bool TryGet(in RenderFrame frame, out T previous, out T current, out bool continuous)
    {
        if (!TryGet(frame, out current))
        {
            previous = default;
            continuous = false;
            return false;
        }

        int slot = frame.PreviousSlot;
        if (slot != frame.CurrentSlot && written[slot] == frame.PreviousSequence)
        {
            previous = values[slot];
            continuous = epochs[slot] == epochs[frame.CurrentSlot];
        }
        else
        {
            previous = current;
            continuous = slot == frame.CurrentSlot;
        }

        return true;
    }
}

/// <summary>
/// Blending for snapshots of things that know how to be blended.
/// </summary>
public static class SnapshotBlending
{
    /// <summary>
    /// What a thing looks like at the moment a frame shows. Render thread.
    /// Across a <see cref="Snapshot{T}.Break"/> it is shown as it was until the frame is all the way at the newer
    /// snapshot, and as it is from then on: put somewhere else at the moment it was put there, never on its way.
    /// </summary>
    /// <returns>False if the thing wasn't published in the newer snapshot of the frame.</returns>
    public static bool TryBlend<T>(this Snapshot<T> snapshot, in RenderFrame frame, out T value) where T : struct, IBlendable<T>
    {
        if (!snapshot.TryGet(frame, out T previous, out T current, out bool continuous))
        {
            value = default;
            return false;
        }

        if (continuous)
            value = frame.Alpha >= 1.0f ? current : frame.Alpha <= 0.0f ? previous : T.Blend(previous, current, frame.Alpha);
        else
            value = frame.Alpha >= 1.0f || frame.PreviousSlot == frame.CurrentSlot ? current : previous;

        return true;
    }
}
