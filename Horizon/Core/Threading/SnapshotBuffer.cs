namespace Horizon.Core.Threading;

/// <summary>
/// Like a <see cref="Snapshot{T}"/>, for what is too big to be copied whole every tick, a list or an array of quads. There
/// is an object of its own for every slot of the <see cref="SnapshotClock"/>, made once and reused, and the simulation
/// fills in the one of the capture that is going on while the renderer reads the ones of its frame, which nobody
/// writes to while it does.
/// <code>
/// // Simulation thread, at the end of the tick
/// if (items.BeginPublish() is { } list) { list.Clear(); list.AddRange(whatIsDrawn); }
///
/// // Render thread
/// if (items.TryGet(RenderFrame.Active, out var previous, out var current, out bool continuous)) Draw(previous, current);
/// </code>
/// </summary>
public sealed class SnapshotBuffer<T> where T : class
{
    private readonly T[] buffers = new T[SnapshotClock.SLOTS];
    private readonly long[] written = new long[SnapshotClock.SLOTS];
    private readonly int[] epochs = new int[SnapshotClock.SLOTS];
    private int epoch;

    /// <param name="create">Makes the object of a slot, called once for each of them.</param>
    public SnapshotBuffer(Func<T> create)
    {
        for (int i = 0; i < buffers.Length; i++)
            buffers[i] = create();
    }

    /// <summary>
    /// Has what is published next not be blended with what was published before it. Simulation thread.
    /// </summary>
    public void Break() => epoch++;

    /// <summary>
    /// The object of the capture that is going on of the engine's clock, to be filled in now. It is part of that
    /// capture from here on. Null if no capture is going on. Simulation thread.
    /// </summary>
    public T? BeginPublish() => BeginPublish(SnapshotClock.Active);

    /// <summary>
    /// The object of the capture that is going on of a clock, to be filled in now. Null if no capture is going on.
    /// Simulation thread.
    /// </summary>
    public T? BeginPublish(SnapshotClock? clock)
    {
        if (clock is null || clock.CaptureSlot is not (>= 0 and var slot))
            return null;

        written[slot] = clock.CaptureSequence;
        epochs[slot] = epoch;
        return buffers[slot];
    }

    /// <summary>
    /// The object of the newer snapshot of a frame. Render thread, read only.
    /// </summary>
    /// <returns>False if nothing was published in it.</returns>
    public bool TryGet(in RenderFrame frame, out T current)
    {
        if (frame.HasSnapshot && written[frame.CurrentSlot] == frame.CurrentSequence)
        {
            current = buffers[frame.CurrentSlot];
            return true;
        }

        current = null!;
        return false;
    }

    /// <summary>
    /// The objects of both snapshots of a frame. Render thread, read only.
    /// </summary>
    /// <param name="previous">The older one, the newer one again if nothing was published in the older one.</param>
    /// <param name="continuous">Whether the two can be blended, both are there and nothing broke in between.</param>
    /// <returns>False if nothing was published in the newer one.</returns>
    public bool TryGet(in RenderFrame frame, out T previous, out T current, out bool continuous)
    {
        if (!TryGet(frame, out current))
        {
            previous = null!;
            continuous = false;
            return false;
        }

        int slot = frame.PreviousSlot;
        if (slot != frame.CurrentSlot && written[slot] == frame.PreviousSequence)
        {
            previous = buffers[slot];
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
