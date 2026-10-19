using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>Hex's one settings file in sections, each side writing its own and keeping the others.</summary>
public class SettingsSectionsTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), $"settings-sections-{Guid.NewGuid():N}.hor");

    [Fact]
    public void The_terminal_writes_its_section_and_keeps_the_editors()
    {
        string file = Temp();
        try
        {
            File.WriteAllText(file, SettingsSections.Write(null, (SettingsSections.EDITOR, new HIDLObject().Add("code_zoom", 1.5f))));
            var terminal = new TerminalSettings(file) { Theme = "midnight" };
            Assert.Null(terminal.Save());

            string text = File.ReadAllText(file);
            var all = SettingsSections.ReadText(text);
            Assert.True(SettingsSections.IsSectioned(all));
            Assert.Equal(1.5f, Assert.IsType<NumberValue>(SettingsSections.Section(all, SettingsSections.EDITOR)["code_zoom"]).Value, 3);
            Assert.Equal("midnight", Assert.IsType<StringValue>(SettingsSections.Section(all, SettingsSections.TERMINAL)["theme"]).Value);
            Assert.Contains("#define dusk_settings", text);

            // the editor writing its section again keeps the terminal's, its help over every key with it
            File.WriteAllText(file, SettingsSections.Write(file, (SettingsSections.EDITOR, new HIDLObject().Add("code_zoom", 2f))));
            text = File.ReadAllText(file);
            Assert.Contains("// " + TerminalSettings.Described[0].Help, text);
            Assert.Equal("midnight", new TerminalSettings(file).Theme);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_file_from_before_the_sections_is_the_terminals_and_comes_out_in_sections()
    {
        string file = Temp();
        try
        {
            File.WriteAllText(file, "let settings = { theme: \"slate\", scrollback: 200 };");
            var terminal = new TerminalSettings(file);
            Assert.Equal("slate", terminal.Theme);
            Assert.Equal(200, terminal.Scrollback);
            terminal.Save();
            var all = SettingsSections.ReadText(File.ReadAllText(file));
            Assert.True(SettingsSections.IsSectioned(all));
            Assert.Empty(SettingsSections.Section(all, SettingsSections.EDITOR));
            Assert.False(all.ContainsKey("theme"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
