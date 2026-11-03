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

    private static readonly UIRect Screen = new(Vector2.Zero, new Vector2(600.0f, 400.0f));

    private static (ScrollPanel Panel, CodeEditor Code) Scene()
    {
        var panel = new ScrollPanel();
        var code = panel.Add(new CodeEditor(string.Join('\n', Enumerable.Range(0, 60).Select(i => $"let line{i} = {i} // [icon:check] a comment"))) { Size = new Vector2(560.0f, 900.0f) });
        return (panel, code);
    }

    private static void Paint(UIDrawList list, UIDrawList? previous, UIComponent root, int tick)
    {
        root.MeasureTree(Skin);
        root.ArrangeTree(Screen);
        list.Previous = previous;
        list.Begin(Skin, tick, 1.0f / 120.0f, tick / 120.0f);
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
}
