using System.Text;

using Horizon.Core.Processes;
using Horizon.UI.Components;

namespace Horizon.Tests.Core;

/// <summary>
/// The pty of Linux and macOS, run for real with /bin/sh. What a terminal needs of it: what the program writes comes
/// out, what is typed goes in, the program knows how big it is and is told when that changes, it starts where it was
/// asked to, and the terminal is the program's controlling one, so Ctrl+C stops what runs in the foreground and not the
/// shell. Nothing on Windows, which has ConPTY (PseudoTerminalTests).
/// </summary>
[Collection(RealProcessesCollection.NAME)]
public class UnixPseudoTerminalTests
{
    private static bool Unix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static bool WaitFor(Func<bool> until, int milliseconds = 15000)
    {
        var started = DateTime.UtcNow;
        while (!until())
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > milliseconds)
                return false;
            Thread.Sleep(10);
        }

        return true;
    }

    // A terminal and a screen it is fed into, and everything it wrote, for what went wrong
    private sealed class Run : IDisposable
    {
        public readonly TerminalScreen Screen;
        public readonly PseudoTerminal Terminal;
        public readonly StringBuilder All = new();
        public volatile bool Exited;

        public Run(string commandLine, int columns = 60, int rows = 10, string? folder = null)
        {
            Screen = new TerminalScreen(columns, rows);
            Terminal = PseudoTerminal.Start(commandLine, folder ?? Path.GetTempPath(), columns, rows);
            Terminal.Output += bytes =>
            {
                lock (Screen)
                {
                    Screen.Feed(bytes);
                    All.Append(Encoding.UTF8.GetString(bytes));
                }
            };
            Terminal.Exited += _ => Exited = true;
        }

        public bool Shows(string text)
        {
            lock (Screen)
                return All.ToString().Contains(text, StringComparison.Ordinal);
        }

        public string Said
        {
            get
            {
                lock (Screen)
                    return All.ToString();
            }
        }

        public void Dispose() => Terminal.Dispose();
    }

    [Fact]
    public void A_command_writes_into_it_knows_its_size_and_ends_with_its_code()
    {
        if (!Unix)
            return;

        using var run = new Run("/bin/sh -c 'echo hello terminal; stty size; test -t 0 && echo a-tty; exit 3'", 40, 6);

        Assert.True(WaitFor(() => run.Exited), $"sh never ended, got: [{run.Said}]");
        Assert.True(run.Shows("hello terminal"), run.Said);
        Assert.True(run.Shows("6 40"), $"it doesn't know its size: [{run.Said}]");
        Assert.True(run.Shows("a-tty"), $"its input isn't a terminal: [{run.Said}]");
        Assert.Equal(3, run.Terminal.ExitCode);
        Assert.False(run.Terminal.IsRunning);
    }

    [Fact]
    public void It_starts_in_the_folder_it_was_given()
    {
        if (!Unix)
            return;

        string folder = Directory.CreateTempSubdirectory("horizon pty ").FullName;
        try
        {
            using var run = new Run("/bin/sh -c pwd", folder: folder);
            Assert.True(WaitFor(() => run.Exited && run.Shows(Path.GetFileName(folder))), $"pwd said: [{run.Said}]");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Typing_reaches_the_shell_and_a_resize_reaches_what_runs_in_it()
    {
        if (!Unix)
            return;

        using var run = new Run("/bin/sh", 60, 10);
        run.Terminal.Write("echo typed-$((40 + 2))\n");
        Assert.True(WaitFor(() => run.Shows("typed-42")), $"what was typed never ran: [{run.Said}]");

        run.Terminal.Resize(50, 9);
        run.Terminal.Write("stty size\n");
        Assert.True(WaitFor(() => run.Shows("9 50")), $"the new size never arrived: [{run.Said}]");

        run.Terminal.Write("exit 0\n");
        Assert.True(WaitFor(() => run.Exited));
        Assert.Equal(0, run.Terminal.ExitCode);
    }

    [Fact]
    public void Control_C_stops_the_program_in_front_and_not_the_shell()
    {
        if (!Unix)
            return;

        using var run = new Run("/bin/sh", 60, 10);
        run.Terminal.Write("echo ready\n");
        Assert.True(WaitFor(() => run.Shows("ready")), run.Said);

        // Job control is on in an interactive shell with a controlling terminal, sleep gets the terminal to itself
        run.Terminal.Write("sleep 30\n");
        Assert.True(WaitFor(() => run.Terminal.RunsAProgram, 5000), $"the shell never had a program in front: [{run.Said}]");

        run.Terminal.Write("\u0003");
        Assert.True(WaitFor(() => !run.Terminal.RunsAProgram, 5000), "Ctrl+C didn't stop sleep, the terminal isn't sleep's controlling one");

        run.Terminal.Write("echo still-here\n");
        Assert.True(WaitFor(() => run.Shows("still-here")), $"the shell went with it: [{run.Said}]");
        Assert.True(run.Terminal.IsRunning);
    }

    [Theory]
    [InlineData("/bin/zsh -l", new[] { "/bin/zsh", "-l" })]
    [InlineData("sh -c 'echo a  b; exit 3'", new[] { "sh", "-c", "echo a  b; exit 3" })]
    [InlineData("\"/Applications/Some App/bin/sh\" -i", new[] { "/Applications/Some App/bin/sh", "-i" })]
    [InlineData("echo \"a \\\"quoted\\\" \\$HOME\"", new[] { "echo", "a \"quoted\" $HOME" })]
    [InlineData("a\\ b  c\t''", new[] { "a b", "c", "" })]
    [InlineData("", new[] { "/bin/sh" })]
    public void A_command_line_is_cut_into_words_the_way_sh_cuts_it(string commandLine, string[] words)
    {
        if (!Unix)
            return;

        Assert.Equal(words, UnixPseudoTerminal.SplitCommandLine(commandLine));
    }
}
