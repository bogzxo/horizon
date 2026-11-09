using System.Numerics;

namespace Horizon.Input;

/// <summary>
/// One finger (or pen) on the screen as of the current simulation tick.
/// </summary>
/// <param name="Id">The contact's own id, never handed to another contact while this one is down.</param>
/// <param name="Position">Where it is in the pixels that are drawn into.</param>
/// <param name="WasPressed">It went down since the last tick.</param>
/// <param name="WasReleased">It came up since the last tick, or was cancelled. It's gone the tick after.</param>
/// <param name="WasCancelled">It didn't come up, the system took it away (a gesture of its own, focus lost). Nothing it was doing should finish.</param>
/// <param name="Kind">A finger or a pen.</param>
public readonly record struct TouchPoint(int Id, Vector2 Position, bool WasPressed, bool WasReleased, bool WasCancelled = false, PointerKind Kind = PointerKind.Touch);

/// <summary>
/// Finger and pen contacts as of the current simulation tick, each by an id of its own so a game can follow several at
/// once. A contact that went down and came up between two ticks is there for one tick with both flags set, a released
/// one stays for the tick that hears of it and is gone the tick after.
/// </summary>
public sealed class Touch
{
    private readonly List<TouchPoint> points = [];
    public IReadOnlyList<TouchPoint> Points { get; }

    public Touch() => Points = points.AsReadOnly();

    /// <summary>How many contacts are down right now, not counting the ones let go of this tick.</summary>
    public int Count
    {
        get
        {
            int count = 0;
            foreach (TouchPoint point in points)
            {
                if (!point.WasReleased) count++;
            }
            return count;
        }
    }

    /// <summary>The contact with an id, one let go of this tick included, false when there's none.</summary>
    public bool TryGet(int id, out TouchPoint point)
    {
        foreach (TouchPoint candidate in points)
        {
            if (candidate.Id != id) continue;
            point = candidate;
            return true;
        }

        point = default;
        return false;
    }

    internal void BeginTick()
    {
        for (int index = points.Count - 1; index >= 0; index--)
        {
            if (points[index].WasReleased) points.RemoveAt(index);
            else points[index] = points[index] with { WasPressed = false };
        }
    }

    internal void Apply(InputEvent input)
    {
        int index = -1;
        for (int candidate = 0; candidate < points.Count; candidate++)
        {
            if (points[candidate].Id != input.PointerId) continue;
            index = candidate;
            break;
        }
        if (input.Kind == InputEventKind.PointerDown)
        {
            var point = new TouchPoint(input.PointerId, input.Position, true, false, false, input.PointerKind);
            if (index < 0) points.Add(point);
            else points[index] = point;
        }
        else if (index >= 0)
        {
            bool ends = input.Kind is InputEventKind.PointerUp or InputEventKind.PointerCancel;
            points[index] = points[index] with
            {
                Position = input.Position,
                WasReleased = points[index].WasReleased || ends,
                WasCancelled = points[index].WasCancelled || input.Kind == InputEventKind.PointerCancel
            };
        }
    }

    /// <summary>Every contact cancelled, for a window that lost the focus and won't hear their ups.</summary>
    internal void CancelAll()
    {
        for (int index = 0; index < points.Count; index++)
        {
            if (!points[index].WasReleased) points[index] = points[index] with { WasReleased = true, WasCancelled = true };
        }
    }
}
