namespace Horizon.Testing;

/// <summary>
/// The names of the checks that didn't hold and the ones that couldn't be made here, as they happen, so a run of every
/// check can say which ones by name and not only how many. <see cref="Checks.TestChecks"/> and
/// <see cref="Checks.UISelfTest"/> note theirs here, the host takes what came in while a check scene ran once it's
/// judged. The checks run one after the other, so whatever is in here belongs to the one that's up.
/// </summary>
internal static class CheckLog
{
    private static readonly Lock gate = new();
    private static List<string> failed = [], skipped = [];

    /// <summary>A check that didn't hold, by the name it was made with.</summary>
    public static void Failed(string title, string name)
    {
        lock (gate) failed.Add($"{title}: {name}");
    }

    /// <summary>A check that wasn't made, and why. Never counted as passing or failing.</summary>
    public static void Skipped(string title, string name, string why)
    {
        Console.WriteLine($"[{title}] skipped: {name}, {why}");
        lock (gate) skipped.Add($"{title}: {name} ({why})");
    }

    /// <summary>Everything noted since the last time, which is left empty for the next check.</summary>
    public static (IReadOnlyList<string> Failed, IReadOnlyList<string> Skipped) Take()
    {
        lock (gate)
        {
            (IReadOnlyList<string>, IReadOnlyList<string>) taken = (failed, skipped);
            failed = [];
            skipped = [];
            return taken;
        }
    }
}
