using Horizon.HIDL.Runtime;
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
}
