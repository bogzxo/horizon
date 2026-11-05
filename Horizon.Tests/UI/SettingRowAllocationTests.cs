using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

public sealed class SettingRowAllocationTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private const string LongKey = "a_setting_key_far_longer_than_the_room_left_beside_its_title";

    [Theory]
    [InlineData("Theme")]
    [InlineData("A very long setting title whose bold words need to be clipped")]
    public void Repeated_measurement_of_clipped_setting_titles_and_keys_allocates_nothing(string title)
    {
        var row = new SettingRow(new SettingsPage(), new UISetting(LongKey, title, UIFormKind.Text, string.Empty), 320);
        for (int i = 0; i < 20; i++) row.MeasureTree(Skin);
        Assert.True(row.KeyCut);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) row.MeasureTree(Skin);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Contains(Labels(row), label => label.Tooltip == LongKey);
        if (title.Length > 20)
            Assert.Contains(Labels(row), label => label.Tooltip == title);
    }

    [Fact]
    public void A_clipped_title_refreshes_its_plain_tooltip_when_its_styled_text_changes()
    {
        const string original = "A long original setting title that must be clipped";
        const string changed = "A different long setting title with highlighted words";
        var row = new SettingRow(new SettingsPage(), new UISetting(LongKey, original, UIFormKind.Text, string.Empty), 320);
        row.MeasureTree(Skin);
        Label title = Assert.Single(Labels(row), label => label.Tooltip == original);

        title.Text = $"[b][color:accent]{changed}[/color][/b]";
        row.MeasureTree(Skin);
        Assert.Equal(changed, title.Tooltip);

        title.Text = "[b]X[/b]";
        row.MeasureTree(Skin);
        Assert.Equal(string.Empty, title.Tooltip);
    }

    private static IEnumerable<Label> Labels(UIComponent root)
    {
        foreach (UIComponent child in root.Children)
        {
            if (child is Label label) yield return label;
            foreach (Label nested in Labels(child)) yield return nested;
        }
    }
}
