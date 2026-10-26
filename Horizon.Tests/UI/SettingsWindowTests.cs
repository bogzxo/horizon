using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>The settings window, one page over every source, the parts in the order the program says.</summary>
public class SettingsWindowTests
{
    private sealed class Source(string section, params UISetting[] settings) : ISettingsSource
    {
        public readonly Dictionary<string, IRuntimeValue> Values = [];

        public string Section => section;
        public IReadOnlyList<UISetting> Settings => settings;
        public IRuntimeValue? Get(string key) => Values.GetValueOrDefault(key);
        public bool IsModified(string key) => Values.ContainsKey(key);
        public void Set(string key, IRuntimeValue value) => Values[key] = value;
        public void Reset(string key) => Values.Remove(key);
    }

    private static UISetting Row(string key, string group) => new(key, key, UIFormKind.Text, string.Empty, group);

    [Fact]
    public void The_parts_go_in_the_order_given_whichever_source_they_came_from_the_rest_after()
    {
        var terminal = new Source("terminal", Row("font", "Terminal · Text"), Row("theme", "Look · Theme"));
        var editor = new Source("editor", Row("zoom", "Editor · Code"), Row("odd", "Other · Things"));

        var window = new SettingsWindow([terminal, editor], () => { }, ["Look", "Editor", "Terminal"]);

        Assert.Equal(["theme", "zoom", "font", "odd"], window.Page.Settings.Select(setting => setting.Key));
    }

    [Fact]
    public void A_change_goes_to_the_source_the_row_came_from()
    {
        var terminal = new Source("terminal", Row("font", "Terminal · Text"));
        var editor = new Source("editor", Row("zoom", "Editor · Code"));
        var window = new SettingsWindow([terminal, editor], () => { });

        window.Page.OnChanged!("zoom", new StringValue("2"));
        Assert.Equal("2", Assert.IsType<StringValue>(editor.Values["zoom"]).Value);
        Assert.Empty(terminal.Values);
    }

    [Fact]
    public void It_opens_sized_to_what_its_in_once_and_a_size_given_after_stays()
    {
        var window = new SettingsWindow([new Source("terminal", Row("font", "Terminal · Text"))], () => { });
        var screen = new Panel();
        screen.Add(window.Window);
        screen.ArrangeTree(new UIRect(System.Numerics.Vector2.Zero, new System.Numerics.Vector2(2000.0f, 1200.0f)));

        screen.UpdateTree(1.0f / 60.0f);
        Assert.Equal(1440.0f, window.Window.Size.X, 0.01f);
        Assert.Equal(936.0f, window.Window.Size.Y, 0.01f);

        window.Window.Size = new System.Numerics.Vector2(800.0f, 500.0f);
        screen.UpdateTree(1.0f / 60.0f);
        Assert.Equal(new System.Numerics.Vector2(800.0f, 500.0f), window.Window.Size);
    }

    [Fact]
    public void Title_actions_sit_left_of_the_x_in_the_order_given_and_a_click_on_one_presses_it()
    {
        var window = new SettingsWindow([new Source("terminal", Row("font", "Terminal · Text"))], () => { });
        Assert.Equal(0, window.Window.TitleActions);

        int file = 0, keys = 0;
        window.AddTitleAction("json", "Open settings file", () => file++);
        window.AddTitleAction("keyboard", "Keyboard shortcuts", () => keys++);
        Assert.Equal(2, window.Window.TitleActions);

        var screen = new Panel();
        screen.Add(window.Window);
        screen.ArrangeTree(new UIRect(System.Numerics.Vector2.Zero, new System.Numerics.Vector2(2000.0f, 1200.0f)));
        screen.UpdateTree(1.0f / 60.0f);
        screen.ArrangeTree(new UIRect(System.Numerics.Vector2.Zero, new System.Numerics.Vector2(2000.0f, 1200.0f)));

        UIRect first = window.Window.TitleActionBounds(0), second = window.Window.TitleActionBounds(1), close = window.Window.CloseBounds;
        Assert.True(first.Max.X <= second.Min.X + 0.01f);
        Assert.Equal((close.Min.Y, close.Max.Y), (first.Min.Y, first.Max.Y));
        Assert.True(first.Width > 0.0f && second.Width > 0.0f);
        Assert.True(second.Max.X <= close.Min.X + 0.01f);

        window.Window.OnPointerDown(second.Center);
        window.Window.OnPointerUp(second.Center);
        Assert.Equal((0, 1), (file, keys));

        // Let go of somewhere else and it wasn't pressed, as with any button
        window.Window.OnPointerDown(first.Center);
        window.Window.OnPointerUp(close.Center + new System.Numerics.Vector2(0.0f, -200.0f));
        Assert.Equal((0, 1), (file, keys));
        Assert.True(window.Window.Visible);
    }

    [Fact]
    public void A_window_without_title_actions_has_none_to_click()
    {
        var plain = new WindowPanel("Plain");
        Assert.Equal(0, plain.TitleActions);
        Assert.True(plain.TitleActionBounds(0).IsEmpty);
    }

    [Fact]
    public void The_section_a_key_is_written_in_is_its_sources()
    {
        var window = new SettingsWindow([new Source("terminal", Row("font", "Terminal · Text")), new Source("editor", Row("zoom", "Editor · Code"))], () => { });
        Assert.Equal("editor", window.SectionOf("zoom"));
        Assert.Equal("terminal", window.SectionOf("font"));
        Assert.Null(window.SectionOf("nothing"));
    }

    [Fact]
    public void A_settings_file_that_doesnt_read_says_why_with_its_line_and_one_that_isnt_there_says_nothing()
    {
        string folder = Path.Combine(Path.GetTempPath(), "horizon-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "settings.hor");
            File.WriteAllText(path, "let settings = {\n    editor: {\n        wrap_code: true,\n        minimap: = false\n    }\n};\n");
            Assert.Null(SettingsSections.TryRead(path, out string? error));
            Assert.NotNull(error);
            Assert.Contains("line", error);
            Assert.Null(SettingsSections.Read(path));

            File.WriteAllText(path, "let settings = {\n    editor: {\n        wrap_code: true\n    }\n};\n");
            var read = SettingsSections.TryRead(path, out error);
            Assert.Null(error);
            Assert.True(Assert.IsType<BooleanValue>(SettingsSections.Section(read!, SettingsSections.EDITOR)["wrap_code"]).Value);

            Assert.Null(SettingsSections.TryRead(Path.Combine(folder, "missing.hor"), out error));
            Assert.Null(error);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
