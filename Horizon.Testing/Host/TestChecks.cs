using System.Text;

namespace Horizon.Testing;

/// <summary>
/// Checks for the parts of the engine that need no pointer to test: each one is a name and whether it held,
/// printed as it is made the same way <see cref="UISelfTest"/> prints its own, and kept for the scene to put on
/// screen.
/// </summary>
internal sealed class TestChecks(string title)
{
    private readonly List<(string Name, bool Passed)> results = [];

    public int Passed => results.Count(result => result.Passed);
    public int Count => results.Count;

    public void Check(string name, Func<bool> check)
    {
        bool passed;
        try
        {
            passed = check();
        }
        catch (Exception e)
        {
            // A check that blows up is a check that failed, and the rest still want their turn.
            Console.WriteLine($"[{title}] {name} threw: {e.Message}");
            passed = false;
        }

        results.Add((name, passed));
        Console.WriteLine($"[{title}] {(passed ? "pass" : "FAIL")}: {name}");
    }

    /// <summary>Prints how many of the checks held.</summary>
    public void Report() => Console.WriteLine($"[{title}] {Passed} of {Count} checks passed.");

    /// <summary>Every check on a line of its own, for a label. Brackets are left out, a label would draw them as icons.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(Passed).Append(" of ").Append(Count).Append(" checks passed");

        foreach (var (name, passed) in results)
            text.Append('\n').Append(passed ? "pass   " : "FAIL   ").Append(name);

        return text.ToString();
    }
}
