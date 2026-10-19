using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Horizon.Tests.UI;

/// <summary>The terminal host's parts that need no window, the commands and their keys files, the settings kept whole, the files.</summary>
public class TerminalHostTests
{
    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), $"terminal-host-test-{Guid.NewGuid():N}-{name}");

    [Fact]
    public void A_keys_file_wins_over_the_key_a_command_asks_for_whenever_it_is_registered()
    {
        string file = Temp("keys.hor");
        try
        {
            File.WriteAllText(file, "let keys = { new_tab: \"Ctrl+T\", split_right: \"\" };");
            var commands = new TerminalCommands();

            commands.Register("split_right", "Split right", () => { }, "Ctrl+Shift+D");
            commands.LoadKeys(file);
            commands.Register("new_tab", "New tab", () => { }, "Ctrl+Shift+T");

            Assert.Equal("Ctrl+T", commands.KeyOf("new_tab"));
            Assert.Equal(string.Empty, commands.KeyOf("split_right"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_later_keys_file_goes_over_an_earlier_one()
    {
        string first = Temp("a.hor"), second = Temp("b.hor");
        try
        {
            File.WriteAllText(first, "let keys = { palette: \"Ctrl+Shift+P\", copy: \"Ctrl+Shift+C\" };");
            File.WriteAllText(second, "let keys = { palette: \"F1\" };");
            var commands = new TerminalCommands();
            commands.Register("palette", "Palette", () => { });
            commands.Register("copy", "Copy", () => { });
            commands.LoadKeys(first, second, Temp("missing.hor"));

            Assert.Equal("F1", commands.KeyOf("palette"));
            Assert.Equal("Ctrl+Shift+C", commands.KeyOf("copy"));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void A_press_finds_its_command_and_an_owner_takes_its_commands_away()
    {
        var commands = new TerminalCommands();
        object plugin = new();
        int ran = 0;
        commands.Register("one", "One", () => ran++, "Ctrl+Shift+1", plugin);
        commands.Register("two", "Two", () => { }, "Ctrl+Shift+2");

        Assert.Equal("one", commands.Match(new UIKeyPress(Key.Number1, true, true, false, false))?.Name);
        Assert.Null(commands.Match(new UIKeyPress(Key.Number1, true, false, false, false)));

        commands.Find("one")!.Run();
        Assert.Equal(1, ran);

        commands.Unregister(plugin);
        Assert.False(commands.Contains("one"));
        Assert.True(commands.Contains("two"));
        Assert.Null(commands.Match(new UIKeyPress(Key.Number1, true, true, false, false)));
    }

    [Fact]
    public void A_keys_file_is_written_so_that_it_reads_back()
    {
        string text = TerminalCommands.WriteKeys([("new_tab", "Ctrl+Shift+T"), ("quit", "")], "about\nthe file");
        Assert.Equal([("new_tab", "Ctrl+Shift+T"), ("quit", "")], TerminalCommands.ReadKeysText(text));
        Assert.StartsWith("// about\n// the file", text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_plus_can_be_the_key()
    {
        Assert.Equal("Ctrl+Shift+=", TerminalShortcut.Tidy("Ctrl+Shift++"));
        Assert.Contains("PgUp", TerminalShortcut.KeyNames());
    }

    [Fact]
    public void Settings_keep_what_they_dont_know_and_write_it_back()
    {
        string file = Temp("settings.hor");
        try
        {
            File.WriteAllText(file, "let settings = { theme: \"slate\", my_plugin_colour: \"teal\", count: 3 };");
            var settings = new TerminalSettings(file);
            Assert.Equal("slate", settings.Theme);
            Assert.Equal("teal", Assert.IsType<StringValue>(settings.Get("my_plugin_colour")).Value);
            Assert.Null(settings.Save());

            var again = new TerminalSettings(file);
            Assert.Equal("teal", Assert.IsType<StringValue>(again.Get("my_plugin_colour")).Value);
            Assert.Equal(3.0f, Assert.IsType<NumberValue>(again.Get("count")).Value);
            Assert.Contains("#define dusk", File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Every_setting_is_described_and_every_description_is_a_setting()
    {
        var settings = new TerminalSettings(null);
        foreach (TerminalSettingInfo info in TerminalSettings.Described)
        {
            Assert.NotNull(settings.Get(info.Key));
            Assert.False(string.IsNullOrWhiteSpace(info.Help));
        }

        var written = TerminalSettings.ReadObject(settings.Write());
        Assert.Equal(TerminalSettings.Described.Select(info => info.Key).Order(), written.Keys.Order());
    }

    [Fact]
    public void A_settings_file_reread_takes_the_change_and_keeps_the_rest()
    {
        string file = Temp("settings.hor");
        try
        {
            var settings = new TerminalSettings(file) { Theme = "light", TextScale = 0.2f };
            settings.Save();
            File.WriteAllText(file, "let settings = { theme: \"midnight\" };");
            Assert.True(settings.Reload());
            Assert.Equal("midnight", settings.Theme);
            Assert.Equal(0.2f, settings.TextScale, 3);

            File.WriteAllText(file, "nonsense {{{");
            Assert.False(settings.Reload());
            Assert.Equal("midnight", settings.Theme);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("file://host/C:/Users/bogz/source", "C:\\Users\\bogz\\source")]
    [InlineData("file:///home/bogz/my%20folder", "/home/bogz/my folder")]
    [InlineData("file://machine/tmp", "/tmp")]
    [InlineData("http://example.com/x", null)]
    public void A_shell_says_its_folder_with_osc_7(string url, string? folder) =>
        Assert.Equal(folder, TerminalScreen.FolderOf(url));

    [Fact]
    public void The_screen_keeps_the_folder_osc_7_said()
    {
        var screen = new TerminalScreen(20, 4);
        Assert.Null(screen.WorkingDirectory);
        screen.Feed("\u001b]7;file://box/C:/work\u001b\\");
        Assert.Equal("C:\\work", screen.WorkingDirectory);
        screen.Feed("\u001b]7;not a url\u0007");
        Assert.Equal("C:\\work", screen.WorkingDirectory);
    }

    [Fact]
    public void A_terminal_file_is_known_by_its_folder_or_its_define()
    {
        string file = Temp("plugin.hor");
        try
        {
            File.WriteAllText(file, "// a plugin of mine\n#define dusk\nlet plugin = {};");
            Assert.True(TerminalFiles.IsTerminalFile(file));
            File.WriteAllText(file, "// a layout\n#define layout\nlet x = 1;");
            Assert.False(TerminalFiles.IsTerminalFile(file));
            Assert.True(TerminalFiles.IsTerminalFile(Path.Combine(TerminalFiles.UserFolder, "keys.hor"), "let keys = {};"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_sessions_file_reads_back_as_the_object_it_declares()
    {
        var session = TerminalSessions.Read("#define dusk\nlet session = { current: 1, tabs: [{ title: \"\", tree: { split: \"right\", ratio: 0.3, first: { profile: \"cmd\", folder: \"C:/\" }, second: { profile: \"pwsh\", folder: \"\" } } }] };");
        Assert.NotNull(session);
        var tabs = Assert.IsType<ListValue>(session!.Value.Properties["tabs"]);
        Assert.Single(tabs.Items);
        Assert.Null(TerminalSessions.Read("broken {{"));
    }

    [Fact]
    public void Every_hook_and_every_core_function_is_described()
    {
        Assert.All(TerminalHost.CoreFunctions, function => Assert.False(string.IsNullOrWhiteSpace(function.Help)));
        Assert.Equal(TerminalHost.CoreFunctions.Count(), TerminalHost.CoreApiNames.Distinct().Count());
        Assert.Contains(TerminalPlugin.Hooks, hook => hook.Name == "on_line");
    }
}
