using System.Collections;
using System.Numerics;

namespace Horizon.UI.Components;

/// <summary>
/// The rows of a scrollback, oldest first, in a ring. A full scrollback lets go of its oldest row for every new one,
/// and a list did that by moving every row it held down by one, ten thousand of them a line of output; a ring lets go
/// of the oldest by moving where it starts.
/// </summary>
internal sealed class ScrollbackRows : IReadOnlyList<TerminalRow>
{
    private const int INITIAL_CAPACITY = 64;

    private TerminalRow[] rows = new TerminalRow[INITIAL_CAPACITY];
    private int start;

    public int Count { get; private set; }

    public TerminalRow this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return rows[Slot(index)];
        }
    }

    private int Slot(int index)
    {
        int slot = start + index;
        return slot < rows.Length ? slot : slot - rows.Length;
    }

    public void Add(TerminalRow row)
    {
        if (Count == rows.Length)
        {
            // Grown with the oldest at the start again
            var grown = new TerminalRow[rows.Length * 2];
            int first = rows.Length - start;
            Array.Copy(rows, start, grown, 0, first);
            Array.Copy(rows, 0, grown, first, start);
            rows = grown;
            start = 0;
        }

        rows[Slot(Count)] = row;
        Count++;
    }

    /// <summary>Lets go of as many of the oldest rows.</summary>
    public void RemoveOldest(int count)
    {
        count = Math.Min(count, Count);
        for (int i = 0; i < count; i++)
            rows[Slot(i)] = null!;
        start = Slot(count);
        Count -= count;
        if (Count == 0)
        {
            start = 0;
            if (rows.Length > INITIAL_CAPACITY)
                rows = new TerminalRow[INITIAL_CAPACITY];
            return;
        }

        // Release excess ring capacity when MaxScrollback shrinks significantly
        if (rows.Length > 256 && Count <= rows.Length / 4)
        {
            int target = Math.Max(INITIAL_CAPACITY, (int)BitOperations.RoundUpToPowerOf2((uint)(Count * 2)));
            if (target < rows.Length)
            {
                var shrunk = new TerminalRow[target];
                int first = Math.Min(Count, rows.Length - start);
                Array.Copy(rows, start, shrunk, 0, first);
                if (first < Count)
                    Array.Copy(rows, 0, shrunk, first, Count - first);
                rows = shrunk;
                start = 0;
            }
        }
    }

    public void Clear()
    {
        if (rows.Length > INITIAL_CAPACITY)
            rows = new TerminalRow[INITIAL_CAPACITY];
        else
            Array.Clear(rows);
        start = 0;
        Count = 0;
    }

    /// <summary>Where a row is, -1 for one that isn't here, looked for from the newest, which is where a row asked about usually is.</summary>
    public int IndexOf(TerminalRow row)
    {
        for (int i = Count - 1; i >= 0; i--)
            if (ReferenceEquals(rows[Slot(i)], row))
                return i;
        return -1;
    }

    public IEnumerator<TerminalRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return rows[Slot(i)];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
