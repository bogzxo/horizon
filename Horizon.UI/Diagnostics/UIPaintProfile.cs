using System.Diagnostics;

using Horizon.UI.Components;

namespace Horizon.UI.Diagnostics;

/// <summary>
/// How long every kind of component spends painting itself and measuring itself for the layout, its own time without
/// what's inside of it, while it's switched on. For finding out which part of a slow page is the slow part. Off it costs a branch a component.
/// Simulation thread.
/// </summary>
public static class UIPaintProfile
{
    /// <summary>Whether components are timed as they paint.</summary>
    public static bool On { get; set; }

    private static readonly Dictionary<Type, (double Ms, long Painted)> byType = [], measuredByType = [];

    // The components painting, inside of each other, with when they started and how long what's inside them took
    private static readonly Stack<(long Started, double Inside)> painting = new();

    /// <summary>Starts over.</summary>
    public static void Reset()
    {
        byType.Clear();
        measuredByType.Clear();
        painting.Clear();
    }

    internal static void Begin() => painting.Push((Stopwatch.GetTimestamp(), 0.0));

    internal static void End(UIComponent component, bool measuring = false)
    {
        var (started, inside) = painting.Pop();
        double took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Type type = component.GetType();
        var counted = measuring ? measuredByType : byType;
        var (ms, painted) = counted.GetValueOrDefault(type);
        counted[type] = (ms + took - inside, painted + 1);

        if (painting.TryPop(out var outer))
            painting.Push((outer.Started, outer.Inside + took));
    }

    /// <summary>The kinds of component that took the longest, the most first, as a line, by every so many ticks.</summary>
    public static string Slowest(long ticks, int most = 10, bool measuring = false) =>
        string.Join(", ", (measuring ? measuredByType : byType).OrderByDescending(kind => kind.Value.Ms).Take(most)
            .Select(kind => $"{kind.Key.Name} {kind.Value.Ms / Math.Max(1, ticks):0.000} ms ({kind.Value.Painted / Math.Max(1, ticks)})"));
}
