using System.Text;

using Horizon.Core.Processes;
using Horizon.UI.Components;

namespace Horizon.Tests.Core;

/// <summary>
/// A process with a terminal attached, run for real, on Windows through ConPTY. Elsewhere the pty is written but not
/// run yet, these pass by doing nothing there until it is.
/// </summary>
[Collection(RealProcessesCollection.NAME)]
public class PseudoTerminalTests
{
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

    [Fact]
    public void A_command_runs_in_a_terminal_and_what_it_writes_comes_out_through_a_screen()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var screen = new TerminalScreen(40, 6);
        var all = new StringBuilder();
        bool exited = false;
        using PseudoTerminal terminal = PseudoTerminal.Start("cmd.exe /c echo hello terminal", Path.GetTempPath(), 40, 6);
        terminal.Output += bytes =>
        {
            lock (screen)
            {
                screen.Feed(bytes);
                all.Append(Encoding.UTF8.GetString(bytes));
            }
        };
        terminal.Exited += _ => exited = true;

        Assert.True(WaitFor(() => { lock (screen) return screen.Text().Contains("hello terminal"); }), $"nothing of what it wrote arrived, running {terminal.IsRunning}, got: [{all}]");
        Assert.True(WaitFor(() => exited), "cmd never ended");
        Assert.Equal(0, terminal.ExitCode);
        Assert.False(terminal.IsRunning);
    }

    [Fact]
    public void Typing_into_it_reaches_the_program()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var screen = new TerminalScreen(60, 8);
        using PseudoTerminal terminal = PseudoTerminal.Start("cmd.exe /q /k", Path.GetTempPath(), 60, 8);
        terminal.Output += bytes => { lock (screen) screen.Feed(bytes); };

        Assert.True(WaitFor(() => { lock (screen) return screen.Text().Contains('>'); }), $"no prompt, running {terminal.IsRunning}, pump {terminal.PumpEnded}, got [{screen.Text()}]");
        terminal.Write("echo typed in\r");
        Assert.True(WaitFor(() => { lock (screen) return screen.Text().Contains("typed in") && screen.Text().Split('\n').Count(line => line.Contains("typed in")) >= 2; }), "the echo never came back: " + screen.Text());

        terminal.Resize(50, 10);
        Assert.Equal((50, 10), (terminal.Columns, terminal.Rows));
        terminal.Write("exit\r");
        Assert.True(WaitFor(() => !terminal.IsRunning), "cmd didn't exit");
    }
}
