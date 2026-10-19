using Horizon.Core.Processes;
using Horizon.UI.Components;

namespace Horizon.Tests.Core;

/// <summary>
/// The programs people live in, run for real in a pseudo terminal against a screen with nothing drawn, the screen's
/// answers sent back the way the view sends them. vim and neovim, less, nano and the Python REPL, whichever of them the
/// machine has (Git for Windows brings vim, less and nano), the ones it hasn't pass by doing nothing. Windows only for
/// now, the Unix pty is unrun.
/// </summary>
[Collection(RealProcessesCollection.NAME)]
public class RealProgramsTests
{
    private sealed class Run : IDisposable
    {
        public readonly TerminalScreen Screen = new(80, 24);
        public readonly PseudoTerminal Process;
        private readonly Lock fed = new();

        public Run(string command, string folder)
        {
            Process = PseudoTerminal.Start(command, folder, 80, 24, new Dictionary<string, string> { ["TERM"] = "xterm-256color" });
            Process.Output += bytes =>
            {
                lock (fed)
                {
                    Screen.Feed(bytes);
                    if (Screen.Responses.Length > 0)
                    {
                        Process.Write(Screen.Responses.ToString());
                        Screen.Responses.Clear();
                    }
                }
            };
        }

        public string Text()
        {
            lock (fed)
                return Screen.AllText();
        }

        public bool Alternate()
        {
            lock (fed)
                return Screen.AlternateScreen;
        }

        public bool WaitFor(Func<bool> until, int milliseconds = 10000)
        {
            var started = DateTime.UtcNow;
            while (!until())
            {
                if ((DateTime.UtcNow - started).TotalMilliseconds > milliseconds)
                    return false;
                Thread.Sleep(20);
            }

            return true;
        }

        public void Dispose() => Process.Dispose();
    }

    private static string? Find(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;

            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string full = Path.Combine(folder.Trim('"'), candidate);
                if (File.Exists(full))
                    return full;
            }
        }

        return null;
    }

    private static string Folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "terminal-programs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "notes.txt"), [.. Enumerable.Range(1, 60).Select(i => $"line {i} of the notes")]);
        return folder;
    }

    private const string GIT_BIN = @"C:\Program Files\Git\usr\bin\";

    [Fact]
    public void Vim_draws_a_file_on_the_alternate_screen_and_gives_it_back()
    {
        if (!OperatingSystem.IsWindows() || Find(GIT_BIN + "vim.exe") is not { } vim)
            return;

        string folder = Folder();
        using var run = new Run($"\"{vim}\" -u NONE -N notes.txt", folder);
        // Windows' own ConPTY repaints a full screen program into its one buffer rather than passing the alternate screen
        // through (Windows Terminal ships a newer ConPTY for that), so which screen it is on isn't asked here
        Assert.True(run.WaitFor(() => run.Text().Contains("line 1 of the notes")), $"vim drew nothing, got [{run.Text()}]");

        run.Process.Write("Gdd");
        Assert.True(run.WaitFor(() => run.Text().Contains("line 59 of the notes") && !run.Text().Contains("line 60 of the notes")), $"G and dd did nothing, got [{run.Text()}]");

        run.Process.Write("\u001b:q!\r");
        Assert.True(run.WaitFor(() => !run.Process.IsRunning), "vim never ended");
        Assert.False(run.Alternate(), "vim left the alternate screen on");
    }

    [Fact]
    public void Neovim_runs_and_answers_its_queries()
    {
        if (!OperatingSystem.IsWindows() || Find("nvim.exe", @"C:\Program Files\Neovim\bin\nvim.exe") is not { } nvim)
            return;

        string folder = Folder();
        using var run = new Run($"\"{nvim}\" --clean notes.txt", folder);
        Assert.True(run.WaitFor(() => run.Text().Contains("line 1 of the notes"), 15000), $"neovim drew nothing, got [{run.Text()}]");

        run.Process.Write(":set number\r");
        Assert.True(run.WaitFor(() => run.Text().Contains("  1 line 1 of the notes")), $"neovim's numbers aren't there, got [{run.Text()}]");

        run.Process.Write(":q!\r");
        Assert.True(run.WaitFor(() => !run.Process.IsRunning), "neovim never ended");
    }

    [Fact]
    public void Less_pages_through_a_file_and_quits()
    {
        if (!OperatingSystem.IsWindows() || Find(GIT_BIN + "less.exe") is not { } less)
            return;

        string folder = Folder();
        using var run = new Run($"\"{less}\" notes.txt", folder);
        Assert.True(run.WaitFor(() => run.Text().Contains("line 1 of the notes")), $"less drew nothing, got [{run.Text()}]");

        run.Process.Write(" ");
        Assert.True(run.WaitFor(() => run.Text().Contains("line 40 of the notes")), $"space didn't page, got [{run.Text()}]");

        run.Process.Write("q");
        Assert.True(run.WaitFor(() => !run.Process.IsRunning), "less never ended");
    }

    [Fact]
    public void Nano_opens_and_leaves_with_control_x()
    {
        if (!OperatingSystem.IsWindows() || Find(GIT_BIN + "nano.exe") is not { } nano)
            return;

        string folder = Folder();
        using var run = new Run($"\"{nano}\" notes.txt", folder);
        Assert.True(run.WaitFor(() => run.Text().Contains("GNU nano") && run.Text().Contains("line 1 of the notes")), $"nano drew nothing, got [{run.Text()}]");

        run.Process.Write("\u0018");
        Assert.True(run.WaitFor(() => !run.Process.IsRunning), $"nano never ended, got [{run.Text()}]");
    }

    [Fact]
    public void The_python_repl_answers_a_line()
    {
        if (!OperatingSystem.IsWindows() || Find("python.exe") is not { } python || python.Contains("WindowsApps"))
            return;

        string folder = Folder();
        using var run = new Run($"\"{python}\" -q", folder);
        Assert.True(run.WaitFor(() => run.Text().Contains(">>>")), $"python said nothing, got [{run.Text()}]");

        run.Process.Write("6 * 7\r");
        Assert.True(run.WaitFor(() => run.Text().Contains("\n42")), $"python didn't answer, got [{run.Text()}]");

        run.Process.Write("exit()\r");
        Assert.True(run.WaitFor(() => !run.Process.IsRunning), "python never ended");
    }
}
