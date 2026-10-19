using Horizon.UI.Components;

namespace Horizon.Tests;

// The dawn command's half in the engine, no shell and no window. What a shell says with the private OSC queued for the
// host, the host's reading of it, a file edited in place of a pane, and cmd given dawn.cmd on its PATH.
public class TerminalDawnTests
{
    [Fact]
    public void What_a_shell_asks_for_is_queued_whole_semicolons_and_all()
    {
        var screen = new TerminalScreen(80, 24);
        screen.Feed("before\u001b]1717;dawn;inline;C:\\odd;name.txt\u0007after");

        Assert.True(screen.TryTakeRequest(out string request));
        Assert.Equal("dawn;inline;C:\\odd;name.txt", request);
        Assert.False(screen.TryTakeRequest(out _));
        Assert.Contains("beforeafter", screen.Text());
    }

    [Fact]
    public void A_file_edited_in_place_is_saved_and_a_change_stops_the_close_once()
    {
        string folder = Path.Combine(Path.GetTempPath(), "horizon-dawn-tests");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "edited.txt");
        File.WriteAllText(file, "one\n");

        var view = new TerminalView();
        Assert.True(view.EditInline(file));
        Assert.Equal("one\n", view.InlineEditor!.Text);
        Assert.False(view.InlineChanged);

        view.InlineEditor.Document.Insert("zero ");
        Assert.True(view.InlineChanged);
        Assert.False(view.CloseInline());
        Assert.NotNull(view.InlineEditor);

        Assert.True(view.SaveInline());
        Assert.Equal("zero one\n", File.ReadAllText(file));
        Assert.False(view.InlineChanged);
        Assert.True(view.CloseInline());
        Assert.Null(view.InlineEditor);

        // A folder isn't edited in place, and a file that isn't there yet is made when it's saved
        Assert.False(view.EditInline(folder));
        string made = Path.Combine(folder, "made", "new.txt");
        if (File.Exists(made)) File.Delete(made);
        Assert.True(view.EditInline(made));
        view.InlineEditor!.Document.Insert("new");
        Assert.True(view.SaveInline());
        Assert.Equal("new", File.ReadAllText(made));
        view.CloseInline(force: true);
    }

    [Fact]
    public void Cmd_has_the_scripts_on_its_path_for_dawn_cmd()
    {
        var cmd = new TerminalProfile("Command Prompt", "cmd.exe", "terminal-cmd", "cmd");
        var environment = TerminalIntegration.Environment(cmd, null, integration: true);

        if (Directory.Exists(TerminalIntegration.Scripts))
        {
            Assert.StartsWith(TerminalIntegration.Scripts + Path.PathSeparator, environment["PATH"]);
            Assert.True(File.Exists(Path.Combine(TerminalIntegration.Scripts, "dawn.cmd")));
        }

        Assert.DoesNotContain("PATH", TerminalIntegration.Environment(cmd, null, integration: false).Keys);
    }
}
