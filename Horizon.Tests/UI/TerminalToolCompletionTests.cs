using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// The tools' words, read out of help shaped like the real thing (git's, dotnet's, cargo's, gh's, npm's) and offered
/// after the tool, with nothing run, the topics are taught by hand.
/// </summary>
public class TerminalToolCompletionTests
{
    private const string GitHelpAll = """
        See 'git help <command>' to read about a specific subcommand

        Main Porcelain Commands
           add                     Add file contents to the index
           checkout                Switch branches or restore working tree files
           commit                  Record changes to the repository
           stash                   Stash the changes in a dirty working directory away

        Command aliases
           co                      = checkout
        """;

    private const string GitStashHelp = """
        usage: git stash list [<log-options>]
           or: git stash show [-u | --include-untracked | --only-untracked] [<diff-options>] [<stash>]
           or: git stash drop [-q | --quiet] [<stash>]
           or: git stash pop [--index] [-q | --quiet] [<stash>]

            -q, --[no-]quiet      be quiet
            --index               try to reinstate the index too
        """;

    private const string DotnetHelp = """
        Usage: dotnet [sdk-options] [command] [command-options] [arguments]

        SDK commands:
          add               Add a package or reference to a .NET project.
          build             Build a .NET project.
                            and this line is the help wrapped round.
          test              Run unit tests using the test runner specified in a .NET project.

        Common options:
          -d|--diagnostics  Enable diagnostic output.
          -h|--help         Show command line help.
          --format=<fmt>    How to print it.
        """;

    private const string CargoHelp = """
        Commands:
            build, b    Compile the current package
            check, c    Analyze the current package and report errors
        """;

    private const string GhHelp = """
        CORE COMMANDS
          auth:          Authenticate gh and git with GitHub
          pr:            Manage pull requests
        """;

    private const string NpmHelp = """
        All commands:

            access, adduser, audit, bugs, cache, ci
        """;

    [Fact]
    public void Gits_list_of_commands_reads_with_its_notes_and_aliases()
    {
        TerminalHelpTopic topic = TerminalHelpParser.Parse(GitHelpAll, "git");
        Assert.Equal(["add", "checkout", "commit", "stash", "co"], topic.Subcommands.Select(item => item.Name));
        Assert.Equal("Record changes to the repository", topic.Subcommands[2].Note);
        Assert.Equal("alias for checkout", topic.Subcommands[4].Note);
    }

    [Fact]
    public void A_subcommands_usage_lines_say_its_own_subcommands_and_no_flags_are_both()
    {
        TerminalHelpTopic topic = TerminalHelpParser.Parse(GitStashHelp, "git stash");
        Assert.Equal(["list", "show", "drop", "pop"], topic.Subcommands.Select(item => item.Name));
        Assert.Contains(topic.Flags, flag => flag.Name == "--quiet" && flag.Note == "be quiet");
        Assert.Contains(topic.Flags, flag => flag.Name == "--no-quiet");
        Assert.Contains(topic.Flags, flag => flag.Name == "-q");
        Assert.Contains(topic.Flags, flag => flag.Name == "--index");
    }

    [Fact]
    public void Dotnets_cargos_ghs_and_npms_shapes_all_read()
    {
        TerminalHelpTopic dotnet = TerminalHelpParser.Parse(DotnetHelp, "dotnet");
        Assert.Equal(["add", "build", "test"], dotnet.Subcommands.Select(item => item.Name));
        Assert.Contains(dotnet.Flags, flag => flag.Name == "--diagnostics");
        Assert.Contains(dotnet.Flags, flag => flag.Name == "-d");
        Assert.Contains(dotnet.Flags, flag => flag.Name == "--format=");

        Assert.Equal(["build", "check"], TerminalHelpParser.Parse(CargoHelp, "cargo").Subcommands.Select(item => item.Name));
        Assert.Equal(["auth", "pr"], TerminalHelpParser.Parse(GhHelp, "gh").Subcommands.Select(item => item.Name));
        Assert.Equal(["access", "adduser", "audit", "bugs", "cache", "ci"], TerminalHelpParser.Parse(NpmHelp, "npm").Subcommands.Select(item => item.Name));
    }

    private static TerminalToolCompletion Taught(out TerminalToolHelp help)
    {
        help = new TerminalToolHelp();
        help.Teach("git", [], TerminalHelpParser.Parse(GitHelpAll, "git"));
        help.Teach("git", ["stash"], TerminalHelpParser.Parse(GitStashHelp, "git stash"));
        // Its own help said nothing that reads, so its flags are stash's, and nothing is run to find that out
        help.Teach("git", ["stash", "pop"], TerminalHelpTopic.Empty);
        help.Teach("dotnet", [], TerminalHelpParser.Parse(DotnetHelp, "dotnet"));
        return new TerminalToolCompletion(["git", "dotnet"], help);
    }

    [Fact]
    public void After_a_tool_its_subcommands_then_their_subcommands_and_flags()
    {
        TerminalToolCompletion tools = Taught(out _);

        Assert.Equal(["checkout", "commit", "co"], tools.Complete(["git"], "c", null)!.Select(item => item.Text));
        Assert.Equal(["pop"], tools.Complete(["git", "stash"], "p", null)!.Select(item => item.Text));
        Assert.Equal(["--quiet"], tools.Complete(["git", "stash", "pop"], "--q", null)!.Select(item => item.Text));
        Assert.Equal(["build"], tools.Complete(["dotnet"], "bu", null)!.Select(item => item.Text));
        Assert.True(tools.Complete(["git"], "c", null)![1].Command);
    }

    [Fact]
    public void A_tool_off_the_list_is_none_of_its_business()
    {
        TerminalToolCompletion tools = Taught(out _);
        Assert.Null(tools.Complete(["deploy"], "pr", null));
        Assert.Null(tools.Complete([], "g", null));
    }

    [Fact]
    public void A_tool_written_with_its_path_or_extension_is_still_the_tool()
    {
        TerminalToolCompletion tools = Taught(out _);
        Assert.Equal(["commit"], tools.Complete([@"C:\Program Files\Git\cmd\git.exe"], "com", null)!.Select(item => item.Text));
    }

    [Fact]
    public void The_shell_completion_puts_the_tools_words_first_and_powershell_only_gets_those()
    {
        TerminalToolCompletion tools = Taught(out _);
        var lookup = new TerminalCommandLookup("cmd", ["git", "dotnet"], sure: true);

        var cmd = new TerminalShellCompletion(new TerminalShellLine("cmd", lookup), tools);
        var found = cmd.Complete("cd x && git che", null, out int start);
        Assert.Equal("checkout", found[0].Text);
        Assert.Equal("cd x && git ".Length, start);

        var powershell = new TerminalShellCompletion(new TerminalShellLine("powershell", lookup), tools, toolsOnly: true);
        Assert.Empty(powershell.Complete("gi", null, out _));
        Assert.Equal(["commit"], powershell.Complete("git com", null, out _).Select(item => item.Text));
    }

    [Fact]
    public void A_commands_words_keep_quotes_together()
    {
        Assert.Equal(["git", "commit", "-m", "a message"], TerminalShellCompletion.CommandWords("echo hi | git commit -m \"a message\" ", 34));
    }

    [Fact]
    public void Only_words_and_flags_are_passed_to_a_tool()
    {
        Assert.True(TerminalToolRunner.IsSafe("stash"));
        Assert.True(TerminalToolRunner.IsSafe("--help"));
        Assert.True(TerminalToolRunner.IsSafe("--format=%(refname:short)"));
        Assert.False(TerminalToolRunner.IsSafe("a; rm -rf ~"));
        Assert.False(TerminalToolRunner.IsSafe("$(whoami)"));
        Assert.False(TerminalToolRunner.IsSafe("x & y"));
    }

    [Fact]
    public void Gits_status_says_the_changed_files_renames_as_the_new_name()
    {
        Assert.Equal(["a.cs", "new.cs", "has space.txt"], TerminalGitValues.ChangedFiles(" M a.cs\nR  old.cs -> new.cs\n?? \"has space.txt\"\n"));
    }
}
