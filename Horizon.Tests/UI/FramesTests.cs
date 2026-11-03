using System.Numerics;
using Horizon.Graphics;
using Horizon.HIDL.Runtime;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// The frames a list paints alongside its quads are the whole of what it painted, resolved back they come to the same
/// quads, runs and glass. Scrolled, clipped, animated, glassy and made of parts, the things the frames have to carry.
/// </summary>
public class FramesTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));

    private sealed class Swatch(Vector4 color, bool textured) : UIComponent
    {
        protected override Vector2 Measure(UISkin skin) => new(100.0f, 20.0f);

        protected override void Paint(UIDrawList list)
        {
            if (textured)
                list.Image(Texture.Invalid, Bounds, Vector2.Zero, new Vector2(16.0f), color);
            else
                list.Box(Bounds, color);
        }
    }

    private sealed class Source(string section, params UISetting[] settings) : ISettingsSource
    {
        public string Section => section;
        public IReadOnlyList<UISetting> Settings => settings;
        public IRuntimeValue? Get(string key) => null;
        public bool IsModified(string key) => false;
        public void Set(string key, IRuntimeValue value) { }
        public void Reset(string key) { }
    }

    // Helper method to lay a tree out and paint it as a module would, moved and scaled the way one can be
    private static UIDrawList Paint(UIComponent root, UIRect screen, Vector2 origin, float scale)
    {
        var list = new UIDrawList();
        root.MeasureTree(Skin);
        root.ArrangeTree(screen);
        list.Begin(Skin, 1, 1.0f / 120.0f, 1.0f);
        list.SetTransform(origin, new Vector2(scale));
        root.PaintTree(list);
        list.End();
        return list;
    }

    private static void Same(UIDrawList list)
    {
        Assert.True(list.Items.Length > 0, "nothing was painted");
        Assert.Null(list.FramesDiffer());
    }

    [Theory]
    [InlineData(0.0f, 1.0f)]
    [InlineData(13.5f, 1.0f)]
    [InlineData(37.25f, 1.25f)]
    public void A_scroll_of_pictures_and_boxes_with_glass_resolves_to_what_was_painted(float offset, float scale)
    {
        var panel = new ScrollPanel();
        var stack = panel.Add(new StackPanel { Direction = UIDirection.Vertical });
        for (int i = 0; i < 12; i++)
        {
            var swatch = stack.Add(new Swatch(new Vector4(i / 12.0f, 0.5f, 0.25f, 1.0f), textured: i % 2 == 0));
            if (i % 3 == 0)
                swatch.Glass = UIGlass.Frosted;
            if (i == 4)
                swatch.VisualOffset = new Vector2(7.0f, -3.0f);
            if (i == 5)
                swatch.VisualScale = new Vector2(0.9f);
        }

        var screen = new UIRect(Vector2.Zero, new Vector2(200.0f, 110.0f));
        Paint(panel, screen, Vector2.Zero, 1.0f);
        panel.Offset = offset;
        Same(Paint(panel, screen, new Vector2(30.0f, 40.0f), scale));
    }

    [Fact]
    public void A_settings_window_resolves_to_what_was_painted()
    {
        var window = new SettingsWindow([new Source("editor",
            new UISetting("zoom", "zoom", UIFormKind.Text, string.Empty, "Editor · Code"),
            new UISetting("wrap", "wrap", UIFormKind.Flag, string.Empty, "Editor · Code"),
            new UISetting("theme", "theme", UIFormKind.Text, string.Empty, "Look · Theme"))], () => { });
        var screen = new Panel();
        screen.Add(window.Window);
        var bounds = new UIRect(Vector2.Zero, new Vector2(1600.0f, 1000.0f));
        screen.ArrangeTree(bounds);
        screen.UpdateTree(1.0f / 60.0f);
        Same(Paint(screen, bounds, Vector2.Zero, 1.0f));
    }

    [Fact]
    public void A_list_gliding_and_a_table_resolve_to_what_was_painted()
    {
        var list = new ListBox { Rows = 10, Items = [.. Enumerable.Range(0, 40).Select(i => $"item {i}")] };
        list.Choose(25);
        list.UpdateTree(1.0f / 120.0f);
        Same(Paint(list, new UIRect(Vector2.Zero, new Vector2(200.0f, 400.0f)), Vector2.Zero, 1.0f));

        var table = new Table
        {
            Columns = [new("Load", 80), new("Value", 80)],
            Rows = [[new("F"), new("1000")], [new("L"), new("2")]],
            RowHeight = 24
        };
        table.Refresh();
        Same(Paint(table, new UIRect(Vector2.Zero, new Vector2(160.0f, 140.0f)), new Vector2(5.0f), 1.5f));
    }
}
