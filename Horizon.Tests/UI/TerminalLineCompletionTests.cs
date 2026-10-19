using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>The list of what finishes a word typed at a cmd or bash prompt, worked out and taken.</summary>
public class TerminalLineCompletionTests
{
    private static TerminalShellCompletion Completion(string kind, params string[] commands) =>
        new(new TerminalShellLine(kind, new TerminalCommandLookup(kind, commands, sure: true)));

    [Fact]
    public void The_first_word_is_finished_with_a_command_and_the_shortest_comes_first()
    {
        var items = Completion("bash", "gitk", "git", "grep").Complete("gi", null, out int start);
        Assert.Equal(0, start);
        Assert.Equal(["git", "gitk"], items.Select(item => item.Text));
        Assert.All(items, item => Assert.True(item.Command));

        // after a pipe it is a command again, after a command it isn't
        Assert.Equal(["grep"], Completion("bash", "grep").Complete("ls | gr", null, out start).Select(item => item.Text));
        Assert.Equal(5, start);
        Assert.Empty(Completion("bash", "grep").Complete("ls gr", null, out _));
    }

    [Fact]
    public void A_path_is_finished_with_what_is_in_its_folder_a_folder_with_its_separator()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"terminal-completion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "source"));
        File.WriteAllText(Path.Combine(folder, "setup.txt"), "a");
        File.WriteAllText(Path.Combine(folder, "source", "main.c"), "a");
        try
        {
            var items = Completion("cmd").Complete("type s", folder, out int start);
            Assert.Equal(5, start);
            Assert.Equal(["source\\", "setup.txt"], items.Select(item => item.Text));
            Assert.True(items[0].Folder);

            Assert.Equal(["source/main.c"], Completion("bash").Complete("cat source/m", folder, out _).Select(item => item.Text));
            Assert.Empty(Completion("bash").Complete("cat ", folder, out _));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_view_shows_the_list_for_what_is_typed_and_tab_types_the_rest()
    {
        var view = new TerminalView { Completer = Completion("bash", "gitk", "git") };
        var typed = new List<string>();
        view.OnInput = typed.Add;
        view.Feed("\u001b]133;A\u0007$ \u001b]133;B\u0007");

        // a letter typed, and the shell writing it back
        view.Type("g");
        view.Feed("g");
        view.ColourLine();
        Assert.Equal(["git", "gitk"], view.Completions.Select(item => item.Text).Take(2));

        Assert.True(view.AcceptCompletion());
        Assert.Equal("it", typed[^1]);
        Assert.Empty(view.Completions);

        // a line run puts it away
        view.Type("i");
        view.Feed("i");
        view.ColourLine();
        Assert.NotEmpty(view.Completions);
        view.Type("\r");
        Assert.Empty(view.Completions);
    }
}
