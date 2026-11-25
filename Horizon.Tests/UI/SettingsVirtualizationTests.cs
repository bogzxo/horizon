using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Viewport windowing of <see cref="SettingsPage"/> rows and <see cref="UIComponent.PaintVersion"/> reuse of its nav
/// links and headings, keeping window dragging and idle painting fast even with hundreds of settings.
/// </summary>
public sealed class SettingsVirtualizationTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));

    private static SettingsPage MakeLargePage(int sections = 15, int perSection = 10)
    {
        var list = new List<UISetting>(sections * perSection);
        for (int s = 0; s < sections; s++)
        {
            string group = $"Part{s / 5} · Section{s}";
            for (int r = 0; r < perSection; r++)
                list.Add(new UISetting($"key_{s}_{r}", $"Setting {s}.{r}", UIFormKind.Flag, $"Controls option {s}.{r} behaviour.", group));
        }

        var values = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal);
        return new SettingsPage
        {
            Settings = list,
            Common = ["key_0_0", "key_5_0"],
            ValueOf = key => values.GetValueOrDefault(key),
            IsModified = values.ContainsKey,
            OnChanged = (key, value) => values[key] = value,
            OnReset = key => values.Remove(key)
        };
    }

    private static void TickLayout(UIComponent root, UIRect area)
    {
        root.UpdateTree(1.0f / 60.0f);
        root.MeasureTree(Skin);
        root.ArrangeTree(area);
    }

    private static void Paint(UIDrawList list, UIDrawList? previous, UIComponent root, UIRect area, int tick)
    {
        root.MeasureTree(Skin);
        root.ArrangeTree(area);
        list.Previous = previous;
        list.Begin(Skin, tick, 1.0f / 60.0f, tick / 60.0f);
        root.PaintTree(list);
        list.End();
    }

    [Fact]
    public void Large_settings_page_only_attaches_visible_window_and_stays_stable_while_dragging()
    {
        var page = MakeLargePage(sections: 15, perSection: 10);
        var window = new WindowPanel("Settings") { Size = new Vector2(1040.0f, 700.0f) };
        window.Add(page);
        page.Fill = UIFill.Both;

        var screen = new Panel();
        screen.Add(window);
        var screenRect = new UIRect(Vector2.Zero, new Vector2(1920.0f, 1080.0f));

        // Warm up build + initial layout so viewport windowing settles.
        for (int i = 0; i < 3; i++)
            TickLayout(screen, screenRect);

        UIComponent rowsHost = page.RowsScroll.Children[0];
        int attachedAtRest = rowsHost.Children.Count;
        Assert.True(attachedAtRest > 0 && attachedAtRest < 50, $"Expected windowed attachment (< 50), got {attachedAtRest}.");

        // Dragging the window changes Position only; attached child count must stay identical.
        for (int step = 1; step <= 10; step++)
        {
            window.Position = new Vector2(step * 12.0f, -step * 6.0f);
            TickLayout(screen, screenRect);
            Assert.Equal(attachedAtRest, rowsHost.Children.Count);
        }

        // GoTo an off-screen section scrolls to it and updates CurrentGroup accurately.
        page.GoTo("Part2 · Section12");
        TickLayout(screen, screenRect);
        TickLayout(screen, screenRect);
        Assert.Equal("Part2 · Section12", page.CurrentGroup);
        Assert.True(page.RowsOffset > 0.0f);
    }

    [Fact]
    public void Settings_nav_and_labels_reuse_identically_when_idle()
    {
        var page = MakeLargePage(sections: 6, perSection: 6);
        var area = new UIRect(Vector2.Zero, new Vector2(1040.0f, 700.0f));
        for (int i = 0; i < 3; i++)
            TickLayout(page, area);

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();

        Paint(a, null, page, area, 1);
        Paint(b, a, page, area, 2);
        Assert.True(b.Reused > 0, "Idle settings page labels and nav items should reuse previous draw records.");

        Paint(fresh, null, page, area, 2);
        Assert.True(b.SameAs(fresh), "Reused settings page draw list must be byte-identical to a fresh paint.");
        Assert.Null(b.FramesDiffer());
    }
}
