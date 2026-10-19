using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>The history of the lines run, the store and the cursor the arrows move, no window and no shell.</summary>
public class TerminalHistoryTests
{
    [Fact]
    public void A_blank_line_and_the_same_line_twice_are_kept_once()
    {
        var history = new TerminalHistory(null);
        Assert.True(history.Add("git status"));
        Assert.False(history.Add("git status"));
        Assert.False(history.Add("   "));
        Assert.True(history.Add("  dir  "));
        Assert.True(history.Add("git status"));

        Assert.Equal(["git status", "dir", "git status"], history.Lines);
    }

    [Fact]
    public void The_oldest_lines_go_past_the_limit()
    {
        var history = new TerminalHistory(null) { Limit = 3 };
        foreach (string line in new[] { "a", "b", "c", "d", "e" })
            history.Add(line);

        Assert.Equal(["c", "d", "e"], history.Lines);
    }

    [Fact]
    public void Up_goes_back_and_down_comes_back_to_what_was_being_typed()
    {
        var history = new TerminalHistory(null);
        history.Add("one");
        history.Add("two");
        var cursor = new TerminalHistory.Cursor();

        Assert.Equal("two", history.Step(cursor, -1, "half typed"));
        Assert.Equal("one", history.Step(cursor, -1, "two"));
        Assert.Null(history.Step(cursor, -1, "one"));
        Assert.Equal("two", history.Step(cursor, 1, "one"));
        Assert.Equal("half typed", history.Step(cursor, 1, "two"));
        Assert.Null(history.Step(cursor, 1, "half typed"));

        cursor.Reset();
        Assert.Equal("two", history.Step(cursor, -1, string.Empty));
    }

    [Fact]
    public void An_empty_history_has_nothing_for_the_arrows()
    {
        var history = new TerminalHistory(null);
        var cursor = new TerminalHistory.Cursor();
        Assert.Null(history.Step(cursor, -1, "anything"));
        Assert.Equal(-1, cursor.At);
    }

    [Fact]
    public void It_is_kept_in_its_file_and_read_back()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dusk-history-test-{Guid.NewGuid():N}.txt");
        try
        {
            var history = new TerminalHistory(file);
            history.Add("cd projects");
            history.Add("dotnet build");
            Assert.Equal("cd projects\ndotnet build\n", File.ReadAllText(file));

            var again = new TerminalHistory(file);
            Assert.Equal(["cd projects", "dotnet build"], again.Lines);

            again.Clear();
            Assert.Equal(string.Empty, File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
