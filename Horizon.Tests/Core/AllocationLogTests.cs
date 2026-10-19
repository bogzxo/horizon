using Horizon.Core.Diagnostics;

namespace Horizon.Tests;

public class AllocationLogTests
{
    private sealed class Outer;
    private sealed class Inner;

    // Kept so the arrays below are not optimised into nothing
    private static object? kept;

    [Fact]
    public void What_a_thing_allocates_is_put_down_to_it_and_not_to_what_it_is_inside_of()
    {
        using var log = new AllocationLog();
        object outer = new Outer(), inner = new Inner();

        // The way an entity measures a child that has a child of its own
        for (int turn = 0; turn < 50; turn++)
        {
            long before = AllocationLog.Begin(out long nested);
            kept = new byte[1000];

            long innerBefore = AllocationLog.Begin(out long innerNested);
            kept = new byte[9000];
            AllocationLog.End(inner, innerBefore, innerNested);

            AllocationLog.End(outer, before, nested);
        }

        string report = log.Report();

        // Fifty times a thousand and fifty times nine thousand, give or take the header of an array
        double outerBytes = BytesOf(report, nameof(Outer)), innerBytes = BytesOf(report, nameof(Inner));
        Assert.InRange(innerBytes / outerBytes, 7.5, 10.0);
    }

    /// <summary>Helper method to read a number out of the report by the name before it. They are in KB a second, only how they compare matters here.</summary>
    private static double BytesOf(string report, string owner)
    {
        int at = report.IndexOf(owner + " ", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{owner} is not in the report: {report}");

        string rest = report[(at + owner.Length + 1)..];
        int end = rest.IndexOfAny([',', ']']);
        return double.Parse(rest[..end], System.Globalization.CultureInfo.CurrentCulture);
    }
}
