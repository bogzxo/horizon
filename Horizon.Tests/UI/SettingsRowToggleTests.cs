using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Clicking anywhere on a boolean (<see cref="UIFormKind.Flag"/>) setting row toggles it cleanly, while direct toggle
/// clicks toggle once, reset resets without re-toggling, right-clicks/cancellations do not toggle, and disabled rows
/// ignore clicks.
/// </summary>
public sealed class SettingsRowToggleTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));

    private static (SettingsPage Page, Dictionary<string, bool> Values, List<(string Key, bool Value)> Changes) MakePage(
        bool initial = false,
        string? note = null,
        string? noteTip = null,
        string? disabledReason = null)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal) { ["flag_opt"] = initial };
        var changes = new List<(string Key, bool Value)>();
        var page = new SettingsPage
        {
            Settings =
            [
                new(
                    "flag_opt",
                    "Enable Feature",
                    UIFormKind.Flag,
                    "Turns the optional feature on. Second sentence keeps a hover tooltip on the help label.",
                    "Editor · General")
            ],
            ValueOf = key => Values.Bool(values.GetValueOrDefault(key)),
            IsModified = key => values.GetValueOrDefault(key) != false,
            OnChanged = (key, value) =>
            {
                bool next = value is BooleanValue { Value: true };
                values[key] = next;
                changes.Add((key, next));
            },
            OnReset = key => values[key] = false,
            NoteOf = _ => note,
            NoteTipOf = _ => noteTip,
            DisabledReasonOf = _ => disabledReason
        };

        Layout(page);
        return (page, values, changes);
    }

    private static void Layout(SettingsPage page)
    {
        var area = new UIRect(Vector2.Zero, new Vector2(1040.0f, 600.0f));
        for (int i = 0; i < 3; i++)
        {
            page.UpdateTree(1.0f / 60.0f);
            page.MeasureTree(Skin);
            page.ArrangeTree(area);
        }
    }

    private static SettingRow RowOf(SettingsPage page, string key) =>
        page.RowItems.OfType<SettingRow>().Single(row => row.Setting.Key == key);

    private static UIComponent ClickAt(SettingsPage page, Vector2 point)
    {
        UIComponent? hit = page.HitTest(point);
        Assert.NotNull(hit);
        if (!hit.EnabledInHierarchy)
            return hit;

        hit.OnPointerDown(point);
        hit.OnPointerUp(point);
        if (ReferenceEquals(page.HitTest(point), hit))
            hit.OnClick();
        return hit;
    }

    [Fact]
    public void Clicking_row_empty_title_help_and_note_toggles_flag_and_preserves_tooltips()
    {
        var (page, values, changes) = MakePage(initial: false, note: "Restart required", noteTip: "Restart to apply.");
        var parts = page.RowParts("flag_opt")!.Value;
        SettingRow row = RowOf(page, "flag_opt");

        // Click empty area on the left side of the row -> turns on
        Vector2 emptyPoint = new(parts.Row.Min.X + 4.0f, parts.Row.Center.Y);
        ClickAt(page, emptyPoint);
        Assert.True(values["flag_opt"]);
        Assert.Single(changes);

        // Click title area -> turns off
        ClickAt(page, parts.Title.Center);
        Assert.False(values["flag_opt"]);
        Assert.Equal(2, changes.Count);

        // Click help label (which has a multi-sentence tooltip) -> turns on, tooltip preserved
        UIComponent helpHit = ClickAt(page, parts.Help.Center);
        Assert.True(values["flag_opt"]);
        Assert.Equal(3, changes.Count);
        Assert.NotEmpty(helpHit.Tooltip);

        // Click note pill (which has a tooltip) -> turns off, tooltip preserved
        Assert.False(row.NoteBounds.IsEmpty);
        UIComponent noteHit = ClickAt(page, row.NoteBounds.Center);
        Assert.False(values["flag_opt"]);
        Assert.Equal(4, changes.Count);
        Assert.Equal("Restart to apply.", noteHit.Tooltip);
    }

    [Fact]
    public void Clicking_directly_on_toggle_toggles_exactly_once()
    {
        var (page, values, changes) = MakePage(initial: false);
        var parts = page.RowParts("flag_opt")!.Value;

        UIComponent hit = ClickAt(page, parts.Control.Center);
        Assert.IsAssignableFrom<ToggleButton>(hit);
        Assert.True(values["flag_opt"]);
        Assert.Single(changes);

        ClickAt(page, parts.Control.Center);
        Assert.False(values["flag_opt"]);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void Clicking_reset_resets_modified_flag_without_retoggling()
    {
        var (page, values, changes) = MakePage(initial: true);
        SettingRow row = RowOf(page, "flag_opt");
        Assert.True(row.Modified);
        Assert.False(row.ResetBounds.IsEmpty);

        UIComponent hit = ClickAt(page, row.ResetBounds.Center);
        Assert.IsAssignableFrom<Button>(hit);
        Assert.False(values["flag_opt"]);
        Assert.Empty(changes);
    }

    [Fact]
    public void Right_click_cancel_and_drag_outside_do_not_toggle()
    {
        var (page, values, changes) = MakePage(initial: false);
        var parts = page.RowParts("flag_opt")!.Value;
        SettingRow row = RowOf(page, "flag_opt");

        // Context menu / right-click path never toggles
        Assert.NotNull(page.MenuOf("flag_opt"));
        row.OnContextMenu?.Invoke(parts.Row.Center);
        Assert.False(values["flag_opt"]);
        Assert.Empty(changes);

        // Touch scroll cancellation clears pending press
        UIComponent hit = page.HitTest(parts.Title.Center)!;
        hit.OnPointerDown(parts.Title.Center);
        hit.OnPointerCancel();
        hit.OnPointerUp(parts.Title.Center);
        Assert.False(values["flag_opt"]);
        Assert.Empty(changes);

        // Releasing outside row bounds does not toggle
        hit.OnPointerDown(parts.Title.Center);
        hit.OnPointerUp(new Vector2(parts.Row.Max.X + 40.0f, parts.Row.Max.Y + 40.0f));
        Assert.False(values["flag_opt"]);
        Assert.Empty(changes);
    }

    [Fact]
    public void Disabled_flag_row_ignores_clicks()
    {
        var (page, values, changes) = MakePage(initial: false, disabledReason: "Disabled by policy");
        var parts = page.RowParts("flag_opt")!.Value;
        SettingRow row = RowOf(page, "flag_opt");

        Assert.False(row.Enabled);
        ClickAt(page, parts.Title.Center);
        row.OnPointerDown(parts.Row.Center);
        row.OnPointerUp(parts.Row.Center);
        Assert.False(values["flag_opt"]);
        Assert.Empty(changes);
    }
}
