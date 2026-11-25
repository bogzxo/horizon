using System.Numerics;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// What a component paints the same as the tick before is taken out of the list painted then (see
/// UIComponent.PaintVersion), and comes out exactly as painting it again would have. The code editor, inside a scroll
/// so it's in a frame and a clip that aren't its own, painted into two lists in turn the way the compositor paints.
/// </summary>
public class PaintReuseTests
{
    // With a font, the code is laid out in rows of it
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private static readonly UISkin AltSkin = new(Skin.Font);

    private static readonly UIRect Screen = new(Vector2.Zero, new Vector2(600.0f, 400.0f));

    private static (ScrollPanel Panel, CodeEditor Code) Scene()
    {
        var panel = new ScrollPanel();
        var code = panel.Add(new CodeEditor(string.Join('\n', Enumerable.Range(0, 60).Select(i => $"let line{i} = {i} // [icon:check] a comment"))) { Size = new Vector2(560.0f, 900.0f) });
        return (panel, code);
    }

    private static void Paint(UIDrawList list, UIDrawList? previous, UIComponent root, int tick, UISkin? skin = null)
    {
        UISkin active = skin ?? Skin;
        root.MeasureTree(active);
        root.ArrangeTree(Screen);
        list.Previous = previous;
        list.Begin(active, tick, 1.0f / 120.0f, tick / 120.0f);
        list.SetTransform(new Vector2(3.0f, 5.0f), Vector2.One);
        root.PaintTree(list);
        list.End();
    }

    [Fact]
    public void A_code_editor_painted_again_unchanged_comes_out_the_same_as_it_was_painted()
    {
        // Settled first, a new editor's first paint isn't what it paints after
        var (panel, _) = Scene();
        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();
        Paint(a, null, panel, 1);
        Paint(b, a, panel, 2);
        Paint(a, b, panel, 3);
        Assert.True(a.Reused > 0, "nothing was taken out of the list before");

        // And painted again without the list before, for what it should have come to
        Paint(fresh, null, panel, 3);
        Assert.Equal(0, fresh.Reused);
        Assert.Equal(fresh.Items.Length, a.Items.Length);
        for (int i = 0; i < a.Items.Length; i++)
        {
            Assert.Equal(fresh.Items[i].Origin, a.Items[i].Origin);
            Assert.Equal(fresh.Items[i].Color, a.Items[i].Color);
            Assert.Equal(fresh.Items[i].TexMin, a.Items[i].TexMin);
        }
        Assert.Null(a.FramesDiffer());
    }

    [Fact]
    public void Spelling_marks_repaint_without_a_document_edit()
    {
        var (panel, code) = Scene();
        var a = new UIDrawList();
        var b = new UIDrawList();
        Paint(a, null, panel, 1);
        Paint(b, a, panel, 2);
        Paint(a, b, panel, 3);
        int withoutMarks = a.Items.Length;

        code.SetSpellingMarks(code.Document.Version, [new UISpellingMark(0, 4, 5)]);
        Paint(b, a, panel, 4);
        Assert.True(b.Items.Length > withoutMarks, "Publishing spelling results should draw new marks.");

        code.SetSpellingMarks(-1, []);
        Paint(a, b, panel, 5);
        Assert.Equal(withoutMarks, a.Items.Length);
    }

    [Fact]
    public void A_code_editor_scrolled_inside_what_it_is_in_is_painted_again()
    {
        var (panel, _) = Scene();
        var a = new UIDrawList();
        var b = new UIDrawList();
        Paint(a, null, panel, 1);
        Paint(b, a, panel, 2);
        panel.Offset = 40.0f;
        Paint(a, b, panel, 3);
        Assert.Equal(0, a.Reused);

        var fresh = new UIDrawList();
        Paint(fresh, null, panel, 3);
        Assert.Equal(fresh.Items.Length, a.Items.Length);
        Assert.Equal(fresh.Items[^1].Origin, a.Items[^1].Origin);
        Assert.Null(a.FramesDiffer());
    }

    [Fact]
    public void TreeView_reuses_identically_when_idle_and_invalidates_on_changes()
    {
        var tree = new TreeView { Size = new Vector2(280.0f, 320.0f) };
        var folder = new UITreeItem("src") { Expanded = true, Badge = "4" };
        for (int i = 0; i < 30; i++)
            folder.Add(new UITreeItem($"File{i}.cs") { Detail = $"{i * 10} B", Badge = i % 3 == 0 ? "M" : string.Empty });
        tree.Items = [folder, new UITreeItem("README.md") { Note = "root", Dot = new Vector4(0.2f, 0.8f, 0.4f, 1.0f) }];

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();

        // Tick 1 warms paintedSkin/chevrons, tick 2 records, tick 3 reuses tick 2
        Paint(a, null, tree, 1);
        Paint(b, a, tree, 2);
        Paint(a, b, tree, 3);
        Assert.True(a.Reused > 0, "Idle TreeView should reuse its previous draw list.");

        Paint(fresh, null, tree, 3);
        Assert.True(a.SameAs(fresh), "Reused TreeView draw list must be byte-identical to a fresh paint.");
        Assert.Null(a.FramesDiffer());

        // Selection change sets revealSelected=true (PaintVersion=-1 on tick 4 so reveal runs and clears),
        // then tick 5 records the settled state and tick 6 reuses it.
        tree.Selected = folder.Children![2];
        Paint(b, a, tree, 4);
        Assert.Equal(0, b.Reused);
        Paint(fresh, null, tree, 4);
        Assert.True(b.SameAs(fresh));

        Paint(a, b, tree, 5);
        Paint(b, a, tree, 6);
        Assert.True(b.Reused > 0);

        // Hover change invalidates
        tree.IsHovered = true;
        Paint(a, b, tree, 7);
        Assert.Equal(0, a.Reused);
        tree.IsHovered = false;
        Paint(b, a, tree, 8);
        Assert.Equal(0, b.Reused);

        // Scroll change invalidates
        tree.ScrollOffset = 80.0f;
        Paint(a, b, tree, 9);
        Assert.Equal(0, a.Reused);
        Paint(fresh, null, tree, 9);
        Assert.True(a.SameAs(fresh));

        // Visible item text/badge mutation invalidates
        folder.Children[5].Text = "Renamed.cs";
        folder.Children[5].Badge = "U";
        Paint(b, a, tree, 10);
        Assert.Equal(0, b.Reused);
        Paint(fresh, null, tree, 10);
        Assert.True(b.SameAs(fresh));

        // Skin/theme change invalidates
        Paint(a, b, tree, 11, AltSkin);
        Assert.Equal(0, a.Reused);
        Paint(fresh, null, tree, 11, AltSkin);
        Assert.True(a.SameAs(fresh));
    }

    [Fact]
    public void TabStrip_reuses_identically_when_idle_and_invalidates_on_changes()
    {
        var strip = new TabStrip { Size = new Vector2(500.0f, 32.0f), Selected = 0 };
        strip.Tabs.Add(new StripTab { Title = "Program.cs", Pinned = true });
        strip.Tabs.Add(new StripTab { Title = "UICompositor.cs", Modified = true });
        strip.Tabs.Add(new StripTab { Title = "TreeView.cs", PresenceColors = [new Vector4(0.3f, 0.7f, 1.0f, 1.0f)] });

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();

        Paint(a, null, strip, 1);
        Paint(b, a, strip, 2);
        Assert.True(b.Reused > 0, "Idle TabStrip should reuse its previous draw list.");

        Paint(fresh, null, strip, 2);
        Assert.True(b.SameAs(fresh), "Reused TabStrip draw list must be byte-identical to a fresh paint.");
        Assert.Null(b.FramesDiffer());

        // Selection change invalidates
        strip.Selected = 1;
        Paint(a, b, strip, 3);
        Assert.Equal(0, a.Reused);
        Paint(fresh, null, strip, 3);
        Assert.True(a.SameAs(fresh));

        // Tab content/state change invalidates
        strip.Tabs[1].Modified = false;
        strip.Tabs[1].Title = "UICompositor.Renamed.cs";
        Paint(b, a, strip, 4);
        Assert.Equal(0, b.Reused);
        Paint(fresh, null, strip, 4);
        Assert.True(b.SameAs(fresh));

        // Hover state change invalidates
        strip.IsHovered = true;
        Paint(a, b, strip, 5);
        Assert.Equal(0, a.Reused);

        // Skin/theme change invalidates
        Paint(b, a, strip, 6, AltSkin);
        Assert.Equal(0, b.Reused);
        Paint(fresh, null, strip, 6, AltSkin);
        Assert.True(b.SameAs(fresh));
    }

    [Fact]
    public void StatusBar_reuses_identically_when_idle_and_invalidates_on_changes()
    {
        bool showErrors = true;
        var bar = new StatusBar { Size = new Vector2(600.0f, 26.0f) };
        var branch = bar.Add("development");
        var errors = bar.Add("2 errors");
        errors.IsVisible = () => showErrors;
        errors.Background = new Vector4(0.8f, 0.2f, 0.2f, 1.0f);
        var caret = bar.Add("Ln 12, Col 4", right: true, pressed: () => { });

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();

        Paint(a, null, bar, 1);
        Paint(b, a, bar, 2);
        Assert.True(b.Reused > 0, "Idle StatusBar should reuse its previous draw list.");

        Paint(fresh, null, bar, 2);
        Assert.True(b.SameAs(fresh), "Reused StatusBar draw list must be byte-identical to a fresh paint.");
        Assert.Null(b.FramesDiffer());

        // Text change invalidates
        caret.Text = "Ln 13, Col 1";
        Paint(a, b, bar, 3);
        Assert.Equal(0, a.Reused);
        Paint(fresh, null, bar, 3);
        Assert.True(a.SameAs(fresh));

        // Dynamic visibility change invalidates
        showErrors = false;
        Paint(b, a, bar, 4);
        Assert.Equal(0, b.Reused);
        Paint(fresh, null, bar, 4);
        Assert.True(b.SameAs(fresh));

        // Style/hairline change invalidates
        bar.Hairline = false;
        branch.Color = new Vector4(0.4f, 0.9f, 0.5f, 1.0f);
        Paint(a, b, bar, 5);
        Assert.Equal(0, a.Reused);
        Paint(fresh, null, bar, 5);
        Assert.True(a.SameAs(fresh));

        // Skin/theme change invalidates
        Paint(b, a, bar, 6, AltSkin);
        Assert.Equal(0, b.Reused);
        Paint(fresh, null, bar, 6, AltSkin);
        Assert.True(b.SameAs(fresh));
    }

    [Fact]
    public void UIDrawList_SameAs_fast_path_matches_equal_lists_and_detects_single_field_changes()
    {
        var a = new UIDrawList();
        var b = new UIDrawList();

        // Empty lists with same skin are equal; different skins are not
        a.Begin(Skin, 1);
        a.End();
        b.Begin(Skin, 2);
        b.End();
        Assert.True(a.SameAs(b));

        b.Begin(AltSkin, 2);
        b.End();
        Assert.False(a.SameAs(b));

        // Identical quads + glass panes are equal
        var rect = new UIRect(new Vector2(10.0f, 10.0f), new Vector2(120.0f, 50.0f));
        var color = new Vector4(0.2f, 0.4f, 0.8f, 1.0f);
        a.Begin(Skin, 3);
        a.Glass(rect, 6.0f, 12.0f, 4.0f);
        a.Rect(rect, color);
        a.End();

        b.Begin(Skin, 4);
        b.Glass(rect, 6.0f, 12.0f, 4.0f);
        b.Rect(rect, color);
        b.End();
        Assert.True(a.SameAs(b));

        // Single quad color difference is detected
        b.Begin(Skin, 5);
        b.Glass(rect, 6.0f, 12.0f, 4.0f);
        b.Rect(rect, color with { X = 0.25f });
        b.End();
        Assert.False(a.SameAs(b));

        // Single quad position difference is detected
        b.Begin(Skin, 6);
        b.Glass(rect, 6.0f, 12.0f, 4.0f);
        b.Rect(new UIRect(new Vector2(11.0f, 10.0f), new Vector2(121.0f, 50.0f)), color);
        b.End();
        Assert.False(a.SameAs(b));

        // Single glass pane field difference is detected
        b.Begin(Skin, 7);
        b.Glass(rect, 6.0f, 14.0f, 4.0f);
        b.Rect(rect, color);
        b.End();
        Assert.False(a.SameAs(b));
    }

    [Fact]
    public void SettingsPage_rows_reuse_painted_labels_and_buttons_across_slider_steps()
    {
        var values = new Dictionary<string, Horizon.HIDL.Runtime.IRuntimeValue>
        {
            ["glass_blur"] = new Horizon.HIDL.Runtime.NumberValue(10.0f)
        };
        var settings = Enumerable.Range(0, 12).Select(i =>
            i == 0
                ? new UISetting("glass_blur", "Glass blur", UIFormKind.Number, "How far glass blurs what is behind it. More detail in the tooltip.", "Appearance · Glass", Least: 0.0f, Most: 64.0f, Step: 1.0f, Slider: true)
                : new UISetting($"row_{i}", $"Setting row {i} with a long enough title to exercise RowTitle", UIFormKind.Text, $"Help sentence {i} that wraps cleanly inside the left column. Second sentence in tooltip.", "Appearance · Glass", Buttons: ["Reset view"]))
            .ToArray();

        var page = new SettingsPage
        {
            Size = Screen.Size,
            Settings = settings,
            ValueOf = key => values.GetValueOrDefault(key),
            IsModified = values.ContainsKey,
            OnChanged = (key, val) => values[key] = val
        };

        page.MeasureTree(Skin);
        page.ArrangeTree(Screen);
        page.UpdateTree(1.0f / 120.0f);

        var a = new UIDrawList();
        var b = new UIDrawList();
        var fresh = new UIDrawList();

        Paint(a, null, page, 1);
        page.UpdateTree(1.0f / 120.0f);
        Paint(b, a, page, 2);
        page.UpdateTree(1.0f / 120.0f);
        Paint(a, b, page, 3);
        Assert.True(a.Reused > 10, $"Untouched SettingsPage rows should reuse painted quads, reused={a.Reused}.");
        Assert.Null(a.FramesDiffer());
        Paint(fresh, null, page, 3);
        Assert.True(a.SameAs(fresh));

        // Stepping the slider only invalidates the slider row's value controls while untouched rows still reuse
        page.Enter("glass_blur", new Horizon.HIDL.Runtime.NumberValue(24.0f));
        page.UpdateTree(1.0f / 120.0f);
        Paint(b, a, page, 4);
        Assert.True(b.Reused > 10, $"Untouched rows should keep reusing during slider drag, reused={b.Reused}.");
        Assert.Null(b.FramesDiffer());
        Paint(fresh, null, page, 4);
        Assert.True(b.SameAs(fresh));
    }
}
