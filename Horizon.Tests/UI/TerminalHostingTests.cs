using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Horizon.Tests.UI;

/// <summary>Keys as keys.hor writes them, a command bound to one, and settings.hor read, written and read again.</summary>
public class TerminalHostingTests
{
    [Theory]
    [InlineData("Ctrl+Shift+T", "Ctrl+Shift+T")]
    [InlineData("shift+ctrl+t", "Ctrl+Shift+T")]
    [InlineData("Ctrl+Alt+1", "Ctrl+Alt+1")]
    [InlineData("Ctrl+Shift+=", "Ctrl+Shift+=")]
    [InlineData("Ctrl+Shift+,", "Ctrl+Shift+,")]
    [InlineData("Ctrl+Shift+Left", "Ctrl+Shift+Left")]
    [InlineData("Ctrl+Shift+PgUp", "Ctrl+Shift+PgUp")]
    [InlineData("F5", "F5")]
    public void A_key_is_written_the_same_however_it_was_written(string written, string tidy) =>
        Assert.Equal(tidy, TerminalShortcut.Tidy(written));

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+T+U")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+42")]
    public void A_text_that_is_no_key_is_none(string written) =>
        Assert.Null(TerminalShortcut.Read(written));

    [Fact]
    public void A_press_matches_its_key_held_exactly_so()
    {
        var key = TerminalShortcut.Read("Ctrl+Shift+T")!.Value;
        Assert.True(TerminalShortcut.Matches(new UIKeyPress(Key.T, true, true, false, false), key));
        Assert.False(TerminalShortcut.Matches(new UIKeyPress(Key.T, true, false, false, false), key));
        Assert.False(TerminalShortcut.Matches(new UIKeyPress(Key.T, true, true, true, false), key));
        Assert.False(TerminalShortcut.Matches(new UIKeyPress(Key.R, true, true, false, false), key));
    }

    [Fact]
    public void A_command_takes_a_key_and_lets_it_go()
    {
        var command = new TerminalCommand("new_tab", "New tab", () => { }, null);
        Assert.True(command.Bind("ctrl+shift+t"));
        Assert.Equal("Ctrl+Shift+T", command.Key);
        Assert.NotNull(command.Parsed);

        Assert.False(command.Bind("Ctrl+Nonsense"));
        Assert.Equal("Ctrl+Shift+T", command.Key);

        Assert.True(command.Bind(string.Empty));
        Assert.Equal(string.Empty, command.Key);
        Assert.Null(command.Parsed);
    }

    [Fact]
    public void Settings_go_round_through_their_file()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dusk-settings-test-{Guid.NewGuid():N}.hor");
        try
        {
            var settings = new TerminalSettings(file)
            {
                Theme = "midnight",
                Profile = "cmd",
                TextScale = 0.2f,
                Animations = false,
                History = false,
                HistorySize = 250,
                Wsl = false
            };
            settings.DisabledPlugins.Add("themes");
            Assert.Null(settings.Save());

            var read = new TerminalSettings(file);
            Assert.Equal("midnight", read.Theme);
            Assert.Equal("cmd", read.Profile);
            Assert.Equal(0.2f, read.TextScale, 3);
            Assert.False(read.Animations);
            Assert.False(read.History);
            Assert.Equal(250, read.HistorySize);
            Assert.False(read.Wsl);
            Assert.Contains("themes", read.DisabledPlugins);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_settings_file_that_says_little_leaves_the_rest_as_it_comes_and_nonsense_is_ignored()
    {
        string file = Path.Combine(Path.GetTempPath(), $"dusk-settings-test-{Guid.NewGuid():N}.hor");
        try
        {
            File.WriteAllText(file, "let settings = { theme: \"light\", text_scale: 99 };");
            var some = new TerminalSettings(file);
            Assert.Equal("light", some.Theme);
            Assert.Equal(0.6f, some.TextScale, 3);
            Assert.True(some.Animations);

            File.WriteAllText(file, "this isn't HIDL {{{");
            var broken = new TerminalSettings(file);
            Assert.Equal("horizon", broken.Theme);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_plugin_sets_and_reads_a_setting_by_its_key()
    {
        var settings = new TerminalSettings(null);
        Assert.True(settings.Set("animations", Values.Bool(false)));
        Assert.Equal(Values.Bool(false), settings.Get("animations"));
        Assert.False(settings.Set("no_such_thing", Values.Bool(true)));
        Assert.Null(settings.Get("no_such_thing"));
    }
}
