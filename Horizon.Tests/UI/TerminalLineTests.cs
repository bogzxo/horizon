using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>The line typed at a prompt coloured, the shells' lexer, the lookup of commands and the view laying it over the cells.</summary>
public class TerminalLineTests
{
    private static string Kinds(string kind, string line, IEnumerable<string> known, bool sure = true, string? folder = null)
    {
        var colourer = new TerminalShellLine(kind, new TerminalCommandLookup(kind, known, sure));
        var kinds = new TerminalLineKind[line.Length];
        colourer.Colour(line, kinds, folder);
        return new string(kinds.Select(Letter).ToArray());
    }

    private static char Letter(TerminalLineKind kind) => kind switch
    {
        TerminalLineKind.Command => 'c', TerminalLineKind.MissingCommand => 'm', TerminalLineKind.Keyword => 'k',
        TerminalLineKind.Flag => 'f', TerminalLineKind.String => 's', TerminalLineKind.OpenString => 'o',
        TerminalLineKind.Variable => 'v', TerminalLineKind.Number => 'n', TerminalLineKind.Operator => 'p',
        TerminalLineKind.Comment => '#', TerminalLineKind.Path => 'u', TerminalLineKind.Folder => 'd', _ => '.'
    };

    // What the kinds say under one word of the line
    private static string Under(string kinds, string line, string word, int from = 0) => kinds.Substring(line.IndexOf(word, from, StringComparison.Ordinal), word.Length);

    [Fact]
    public void A_command_the_shell_has_is_green_and_one_it_hasnt_is_red()
    {
        Assert.Equal("ccc.......", Kinds("bash", "git status", ["git"]));
        Assert.Equal("mmm.......", Kinds("bash", "gti status", ["git"]));
        Assert.Equal("..........", Kinds("bash", "gti status", ["git"], sure: false));
    }

    [Fact]
    public void Every_command_of_a_pipeline_is_looked_up_and_a_redirection_is_followed_by_a_file()
    {
        const string line = "git log -n 5 --oneline | nope > out.txt";
        string kinds = Kinds("bash", line, ["git"]);
        Assert.Equal("ccc", Under(kinds, line, "git"));
        Assert.Equal("ff", Under(kinds, line, "-n"));
        Assert.Equal("n", Under(kinds, line, "5"));
        Assert.Equal("fffffffff", Under(kinds, line, "--oneline"));
        Assert.Equal("p", Under(kinds, line, "|"));
        Assert.Equal("mmmm", Under(kinds, line, "nope"));
        Assert.Equal("p", Under(kinds, line, ">"));
        Assert.Equal(".......", Under(kinds, line, "out.txt"));
    }

    [Fact]
    public void Strings_closed_and_open_variables_and_comments()
    {
        const string line = "echo \"hi there\" $HOME ${X} 'open";
        string kinds = Kinds("bash", line, []);
        Assert.Equal("cccc", Under(kinds, line, "echo"));
        Assert.Equal("ssssssssss", Under(kinds, line, "\"hi there\""));
        Assert.Equal("vvvvv", Under(kinds, line, "$HOME"));
        Assert.Equal("vvvv", Under(kinds, line, "${X}"));
        Assert.Equal("ooooo", Under(kinds, line, "'open"));

        Assert.Equal("ccc.######", Kinds("bash", "git # note", ["git"]));
        Assert.Equal("######", Kinds("cmd", "rem hi", []));
    }

    [Fact]
    public void An_assignment_and_sudo_come_before_the_command()
    {
        const string line = "NAME=1 sudo nope";
        string kinds = Kinds("bash", line, []);
        Assert.Equal("vvvvvv", Under(kinds, line, "NAME=1"));
        Assert.Equal("kkkk", Under(kinds, line, "sudo"));
        Assert.Equal("mmmm", Under(kinds, line, "nope"));
    }

    [Fact]
    public void Cmd_has_its_switches_and_its_percent_variables()
    {
        const string line = "dir /b %TEMP%";
        string kinds = Kinds("cmd", line, []);
        Assert.Equal("ccc", Under(kinds, line, "dir"));
        Assert.Equal("ff", Under(kinds, line, "/b"));
        Assert.Equal("vvvvvv", Under(kinds, line, "%TEMP%"));
    }

    [Fact]
    public void Powershell_has_its_blocks_and_its_cmdlets()
    {
        const string line = "Get-Thing -Recurse | ForEach-Object { $_.Name }";
        string kinds = Kinds("powershell", line, ["Get-Thing", "ForEach-Object"]);
        Assert.Equal("ccccccccc", Under(kinds, line, "Get-Thing"));
        Assert.Equal("ffffffff", Under(kinds, line, "-Recurse"));
        Assert.Equal("cccccccccccccc", Under(kinds, line, "ForEach-Object"));
        Assert.Equal("p", Under(kinds, line, "{"));
        Assert.Equal("vv", Under(kinds, line, "$_"));
    }

    [Fact]
    public void A_file_or_folder_that_is_there_is_underlined()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"terminal-line-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        try
        {
            const string line = "cat a.txt sub nothing";
            string kinds = Kinds("bash", line, ["cat"], folder: folder);
            Assert.Equal("uuuuu", Under(kinds, line, "a.txt"));
            Assert.Equal("ddd", Under(kinds, line, "sub"));
            Assert.Equal(".......", Under(kinds, line, "nothing"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_lookup_offers_what_starts_with_what_is_typed()
    {
        var lookup = new TerminalCommandLookup("bash", ["git", "gitk", "grep"], sure: true);
        Assert.Contains("gitk", lookup.StartingWith("gi"));
        Assert.DoesNotContain("grep", lookup.StartingWith("gi"));
    }

    [Fact]
    public void Cmd_says_its_marks_and_its_folder_through_its_prompt()
    {
        var profile = new TerminalProfile("Command Prompt", "cmd.exe", id: "cmd");
        var environment = TerminalIntegration.Environment(profile, null, integration: true);
        Assert.StartsWith("$e]133;A$e\\", environment["PROMPT"]);
        Assert.EndsWith("$e]133;B$e\\", environment["PROMPT"]);
        Assert.Empty(TerminalIntegration.Environment(profile, null, integration: false));

        var wsl = new TerminalProfile("Ubuntu", "wsl.exe -d Ubuntu", id: "wsl:Ubuntu");
        var passed = TerminalIntegration.Environment(wsl, @"C:\Users\me\commands.txt", integration: true);
        Assert.Equal("C:/Users/me/commands.txt", passed["DUSK_COMMANDS_FILE"]);
        Assert.EndsWith("DUSK_COMMANDS_FILE/p", passed["WSLENV"]);
    }

    [Fact]
    public void A_cmd_prompt_makes_blocks_with_the_output_worked_out()
    {
        // what cmd writes with that prompt, the marks and the folder round C:\work>, a command, its output, the next prompt
        var screen = new TerminalScreen(40, 10);
        screen.Feed("\u001b]133;A\u001b\\\u001b]9;9;C:\\work\u001b\\C:\\work>\u001b]133;B\u001b\\dir\r\n a.txt\r\n\r\n");
        screen.Feed("\u001b]133;A\u001b\\\u001b]9;9;C:\\work\u001b\\C:\\work>\u001b]133;B\u001b\\");

        Assert.Equal("C:\\work", screen.WorkingDirectory);
        var blocks = screen.Blocks();
        Assert.Equal(2, blocks.Count);
        Assert.Equal("dir", screen.CommandOf(blocks[0]));
        Assert.Equal("a.txt", screen.OutputOf(blocks[0]).Trim());
    }

    [Fact]
    public void A_view_colours_the_line_typed_after_the_prompt_and_nothing_else()
    {
        var view = new TerminalView { LineColourer = new TerminalShellLine("bash", new TerminalCommandLookup("bash", ["git"], true)) };
        view.Feed("\u001b]133;A\u0007git $ \u001b]133;B\u0007gti log");
        view.ColourLine();

        Assert.Equal("gti log", view.LineText);
        Assert.Equal("mmm....", view.LineKindsText());
        Assert.Equal(TerminalLineKind.None, view.LineKindAt(-1));

        // run, the line is no longer at a prompt and nothing is coloured
        view.Feed("\r\n\u001b]133;C\u0007");
        view.ColourLine();
        Assert.Null(view.Line);
    }
}
