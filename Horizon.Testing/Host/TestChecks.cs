using System.Text;

namespace Horizon.Testing;

/// <summary>
/// A dead simple checklist, for examples that check the engine does what it says on the tin without needing a
/// pointer (the UI ones that click about use <see cref="UISelfTest"/>). Every check is a name and whether it held.
/// It's printed the moment it's made, so a run from the command line shows what broke, and kept for the scene to
/// put on screen.
/// <code>
/// var checks = new TestChecks("Tweens");
/// checks.Check("ends where it was told to", () => tweened == 10.0f);
/// checks.Report();
/// resultsLabel.Text = checks.Describe();
/// </code>
/// </summary>
internal sealed class TestChecks(string title)
{
    private readonly List<(string Name, bool Passed)> results = [];

    public int Passed => results.Count(result => result.Passed);
    public int Count => results.Count;

    /// <summary>Runs a check right now and notes how it went.</summary>
    public void Check(string name, Func<bool> check)
    {
        bool passed;
        try
        {
            passed = check();
        }
        catch (Exception e)
        {
            // A check that blows up is a check that failed. Note it and crack on, one shit check shouldn't take
            // the rest down with it
            Console.WriteLine($"[{title}] {name} threw: {e.Message}");
            passed = false;
        }

        results.Add((name, passed));
        Console.WriteLine($"[{title}] {(passed ? "pass" : "FAIL")}: {name}");
    }

    /// <summary>Prints how many of the checks held.</summary>
    public void Report() => Console.WriteLine($"[{title}] {Passed} of {Count} checks passed.");

    /// <summary>
    /// Every check on a line of its own, for a label. No brackets in here on purpose: a label draws [name] as an
    /// icon whenever the skin has one by that name.
    /// </summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(Passed).Append(" of ").Append(Count).Append(" checks passed");

        foreach (var (name, passed) in results)
            text.Append('\n').Append(passed ? "pass   " : "FAIL   ").Append(name);

        return text.ToString();
    }
}
