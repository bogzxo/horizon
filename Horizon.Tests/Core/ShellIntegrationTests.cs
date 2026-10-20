using Horizon.Core.Processes;
using Horizon.UI.Components;

namespace Horizon.Tests.Core;

/// <summary>
/// The shells started with the terminal's integration scripts, run for real, saying their marks and their folder, read
/// into blocks by a screen with nothing drawn. And the marks on their own, fed by hand.
/// </summary>
[Collection(RealProcessesCollection.NAME)]
public class ShellIntegrationTests
{
    private static bool WaitFor(Func<bool> until, int milliseconds = 15000, Action? tick = null)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            tick?.Invoke();
            if (until())
                break;
            if ((DateTime.UtcNow - started).TotalMilliseconds > milliseconds)
                return false;
            Thread.Sleep(20);
        }

        return true;
    }

    private static (TerminalScreen Screen, PseudoTerminal Process, Lock Fed) Start(TerminalProfile profile)
    {
        var screen = new TerminalScreen(100, 30) { LateMarks = OperatingSystem.IsWindows() };
        var fed = new Lock();
        PseudoTerminal process = PseudoTerminal.Start(TerminalIntegration.Command(profile), Path.GetTempPath(), 100, 30);
        process.Output += bytes =>
        {
            lock (fed)
            {
                screen.Feed(bytes);
                if (screen.Responses.Length > 0)
                {
                    process.Write(screen.Responses.ToString());
                    screen.Responses.Clear();
                }
            }
        };
        return (screen, process, fed);
    }

    private static void Shell_says_its_marks(TerminalProfile profile, string failing)
    {
        var (screen, process, fed) = Start(profile);
        void Settle()
        {
            lock (fed)
                screen.SettleIfQuiet();
        }

        using (process)
        {
            Assert.True(WaitFor(() => { lock (fed) return screen.MarksSeen && screen.AtPrompt; }, tick: Settle), $"{profile.Name} said no marks, got [{screen.AllText()}]");
            lock (fed)
                Assert.NotNull(screen.WorkingDirectory);

            process.Write("echo dusk-marks\r");
            Assert.True(WaitFor(() => { lock (fed) return screen.Blocks().Count >= 2 && screen.AtPrompt; }, tick: Settle), $"no block for the command, got [{screen.AllText()}]");

            process.Write(failing + "\r");
            Assert.True(WaitFor(() => { lock (fed) return screen.Blocks().Count >= 3 && screen.Blocks()[^2].Exit is not null && screen.AtPrompt; }, tick: Settle), $"no block for the failing one, got [{screen.AllText()}]");

            lock (fed)
            {
                var blocks = screen.Blocks();
                TerminalBlock echo = blocks[^3], failed = blocks[^2];
                Assert.Equal("echo dusk-marks", screen.CommandOf(echo));
                Assert.Equal("dusk-marks", screen.OutputOf(echo));
                Assert.Equal(0, echo.Exit);
                Assert.False(echo.Guessed);
                Assert.Equal(failing, screen.CommandOf(failed));
                Assert.True(failed.Failed, $"the failing command's exit was {failed.Exit}");
                Assert.NotNull(screen.Input);
            }
        }
    }

    [Fact]
    public void Powershell_says_its_marks_with_the_script()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Shell_says_its_marks(new TerminalProfile("Windows PowerShell", "powershell.exe -NoLogo", "terminal-powershell", "powershell"), "cmd /c exit 3");
    }

    [Fact]
    public void Git_bash_says_its_marks_with_the_script()
    {
        const string bash = @"C:\Program Files\Git\bin\bash.exe";
        if (!OperatingSystem.IsWindows() || !File.Exists(bash))
            return;

        Shell_says_its_marks(new TerminalProfile("Git Bash", $"\"{bash}\" --login -i", "terminal-bash", "git_bash"), "false");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_command_that_writes_nothing_leaves_the_next_one_whole(bool late)
    {
        // the silent one's output starts on the row after it, which is where the next prompt goes
        var screen = new TerminalScreen(40, 10) { LateMarks = late };
        void Feed(string text)
        {
            screen.Feed(text);
            screen.SettleMarks();
        }

        Feed("\u001b]133;A\u0007$ \u001b]133;B\u0007");
        Feed("true\r\n\u001b]133;C\u0007");
        Feed("\u001b]133;D;0\u0007\u001b]133;A\u0007$ \u001b]133;B\u0007");
        Feed("echo hi\r\n\u001b]133;C\u0007hi\r\n");
        Feed("\u001b]133;D;0\u0007\u001b]133;A\u0007$ \u001b]133;B\u0007");

        var blocks = screen.Blocks();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("true", screen.CommandOf(blocks[0]));
        Assert.Equal(string.Empty, screen.OutputOf(blocks[0]));
        Assert.Equal("echo hi", screen.CommandOf(blocks[1]));
        Assert.Equal("hi", screen.OutputOf(blocks[1]));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void A_clear_takes_the_blocks_off_the_screen_it_blanks(bool scrollbackToo, bool scrolledFirst)
    {
        // cls is ESC[2J (and ESC[3J for the scrollback), then cls's own exit code, then the new prompt. The bars of the
        // commands before stayed down the side of the blank screen, and cls's exit code put one back on its blank row
        var screen = new TerminalScreen(40, 8);
        screen.Feed("\u001b]133;A\u0007$ \u001b]133;B\u0007ls\r\n\u001b]133;C\u0007a.txt\r\n\u001b]133;D;0\u0007");
        if (scrolledFirst)
            screen.Feed("\u001b]133;A\u0007$ \u001b]133;B\u0007dir\r\n\u001b]133;C\u0007" + string.Concat(Enumerable.Range(0, 12).Select(i => $"line {i}\r\n")) + "\u001b]133;D;0\u0007");
        screen.Feed("\u001b]133;A\u0007$ \u001b]133;B\u0007false\r\n\u001b]133;C\u0007\u001b]133;D;1\u0007");
        screen.Feed("\u001b]133;A\u0007$ \u001b]133;B\u0007cls\r\n\u001b]133;C\u0007");
        screen.Feed("\u001b[2J" + (scrollbackToo ? "\u001b[3J" : string.Empty) + "\u001b[H");
        screen.Feed("\u001b]133;D;0\u0007\u001b]133;A\u0007$ \u001b]133;B\u0007");

        int top = screen.Scrollback.Count;
        var blocks = screen.Blocks();
        Assert.True(screen.AtPrompt);

        // On the screen there's the new prompt and nothing else, no bar of what was before it, red or green
        var onScreen = blocks.Where(block => block.EndRow >= top).ToList();
        Assert.Single(onScreen);
        Assert.Equal(top, onScreen[0].PromptRow);
        Assert.Null(onScreen[0].Exit);
        Assert.All(screen.Grid.Skip(1), row => Assert.True(row.Mark.IsEmpty));

        // What went up into the scrollback before the clear is still there and still ends where it did
        Assert.All(blocks.Where(block => block.EndRow < top), block => Assert.True(block.PromptRow < top));
    }

    [Theory]
    [InlineData(20, 60)]
    [InlineData(60, 20)]
    [InlineData(20, 13)]
    public void A_prompt_wrapped_at_one_width_keeps_where_its_input_starts_at_another(int from, int to)
    {
        // A pane that opened narrow had its long prompt wrapped, and widened the input was put at the first row's
        // last column, the command read from half way along the prompt
        var screen = new TerminalScreen(from, 10);
        string prompt = "PS C:\\a\\long\\path\\here> ";
        screen.Feed("\u001b]133;A\u0007" + prompt + "\u001b]133;B\u0007echo hi\r\n\u001b]133;C\u0007hi\r\n\u001b]133;D;0\u0007");
        screen.Feed("\u001b]133;A\u0007" + prompt + "\u001b]133;B\u0007");

        screen.Resize(to, 10);
        var blocks = screen.Blocks();
        Assert.Equal(2, blocks.Count);
        Assert.Equal("echo hi", screen.CommandOf(blocks[0]));
        Assert.True(screen.AtPrompt);
        Assert.Equal(prompt.Length % to, screen.Input!.Value.Column);
    }

    [Fact]
    public void A_command_that_never_goes_quiet_is_not_the_prompt()
    {
        // A TUI with a spinner never stops writing long enough for the late marks to settle, and
        // the screen was taken for the shell's prompt the whole time, Up and Down going to the history
        var screen = new TerminalScreen(40, 10) { LateMarks = true };
        screen.Feed("\u001b]133;A\u0007\u001b]133;B\u0007$ ");
        screen.SettleMarks();
        Assert.True(screen.AtPrompt);

        screen.Feed("spin\r\n\u001b]133;C\u0007 thinking");
        Assert.False(screen.AtPrompt);
        Assert.False(Horizon.UI.Components.TerminalHost.AtShellPrompt(screen));
    }

    [Fact]
    public void A_clear_the_way_ConPTY_draws_it_takes_the_blocks_too()
    {
        // ConPTY never says ESC[2J for cls, it clears the scrollback and draws the screen again from the top, the prompt
        // over the first row (which had a prompt with an exit code once) and every other row erased from its start
        var screen = new TerminalScreen(40, 6) { LateMarks = true };
        void Feed(string text)
        {
            screen.Feed(text);
            screen.SettleMarks();
        }

        Feed("\u001b]133;A\u0007\u001b]133;B\u0007$ ");
        Feed("ls\r\n\u001b]133;C\u0007a.txt\r\n\u001b]133;D;0\u0007\u001b]133;A\u0007\u001b]133;B\u0007$ ");
        Feed("false\r\n\u001b]133;C\u0007\u001b]133;D;1\u0007\u001b]133;A\u0007\u001b]133;B\u0007$ ");
        Feed("cls\r\n");
        screen.Feed("\u001b]133;C\u0007\u001b[3J\u001b]133;D;0\u0007\u001b]133;A\u0007\u001b]133;B\u0007");
        screen.Feed("\u001b[H$ \u001b[K" + string.Concat(Enumerable.Repeat("\r\n\u001b[K", 5)) + "\u001b[1;3H");
        screen.SettleMarks();

        var blocks = screen.Blocks();
        Assert.Single(blocks);
        Assert.Equal(0, blocks[0].PromptRow);
        Assert.Null(blocks[0].Exit);
        Assert.Equal(-1, blocks[0].OutputRow);
        Assert.True(screen.AtPrompt);
    }

    [Fact]
    public void Marks_fed_by_hand_make_blocks_and_survive_a_resize()
    {
        var screen = new TerminalScreen(40, 10);
        screen.Feed("\u001b]133;A\u0007$ \u001b]133;B\u0007ls\r\n\u001b]133;C\u0007a.txt\r\nb.txt\r\n\u001b]133;D;0\u0007");
        screen.Feed("\u001b]133;A\u0007$ \u001b]133;B\u0007false\r\n\u001b]133;C\u0007\u001b]133;D;1\u0007\u001b]133;A\u0007$ \u001b]133;B\u0007");

        var blocks = screen.Blocks();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("ls", screen.CommandOf(blocks[0]));
        Assert.Equal("a.txt\nb.txt", screen.OutputOf(blocks[0]));
        Assert.Equal(0, blocks[0].Exit);
        Assert.Equal("false", screen.CommandOf(blocks[1]));
        Assert.True(blocks[1].Failed);
        Assert.Null(blocks[2].Exit);
        Assert.True(screen.AtPrompt);
        Assert.Equal((4, 2), screen.Input);

        screen.Resize(20, 6);
        var after = screen.Blocks();
        Assert.Equal(3, after.Count);
        Assert.Equal("ls", screen.CommandOf(after[0]));
        Assert.True(after[1].Failed);
        Assert.True(screen.AtPrompt);
    }

    [Fact]
    public void A_shell_that_says_nothing_has_its_prompts_guessed()
    {
        var screen = new TerminalScreen(40, 10);
        screen.Feed("C:\\> dir\r\n");
        screen.GuessPrompt();
        screen.Feed(" a.txt\r\nC:\\> ");
        screen.GuessPrompt();

        var blocks = screen.Blocks();
        Assert.Equal(2, blocks.Count);
        Assert.True(blocks[0].Guessed);
        Assert.False(screen.MarksSeen);
    }

    [Theory]
    [InlineData("pwsh", "pwsh.exe -NoLogo", "powershell")]
    [InlineData("cmd", "cmd.exe", "cmd")]
    [InlineData("git_bash", "\"C:\\Program Files\\Git\\bin\\bash.exe\" --login -i", "bash")]
    [InlineData("wsl:Ubuntu", "wsl.exe -d Ubuntu", "bash")]
    public void A_profile_is_known_by_the_shell_it_is(string id, string command, string kind) =>
        Assert.Equal(kind, TerminalIntegration.KindOf(new TerminalProfile(id, command, id: id)));

    [Fact]
    public void Paths_are_written_as_the_shells_have_them()
    {
        Assert.Equal("/c/Users/bogz/dusk.bash", TerminalIntegration.MsysPath(@"C:\Users\bogz\dusk.bash"));
        Assert.Equal("/mnt/c/x/y", TerminalIntegration.WslPath(@"C:\x\y"));
    }
}
