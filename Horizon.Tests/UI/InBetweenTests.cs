using System.Numerics;
using Horizon.Graphics;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Where things are on screen halfway between two ticks, what the UI's frames between ticks have to get right. Two
/// ticks are painted, the frame halfway between them is put together the way the compositor does it, and a quad is
/// found by the colour it was painted in, so these say what's on screen and not how the quads were told apart. Each
/// is one of the bugs in notes/ui-interpolation-plan.md, and skipped until the stage of it that fixes it lands.
/// </summary>
public class InBetweenTests
{
    private const string STAGE_2 = "Fails until UI frames stage 2, see notes/ui-interpolation-plan.md";
    private const string STAGE_3 = "Fails until UI frames stage 3, see notes/ui-interpolation-plan.md";

    private static readonly UISkin Skin = new(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));

    /// <summary>A block of one colour as big as it is put, a picture's worth of texels when it's textured.</summary>
    private sealed class Swatch(Vector4 color, bool textured = false) : UIComponent
    {
        public float Height { get; init; } = 20.0f;

        protected override Vector2 Measure(UISkin skin) => new(100.0f, Height);

        protected override void Paint(UIDrawList list)
        {
            if (textured)
                list.Image(Texture.Invalid, Bounds, Vector2.Zero, new Vector2(16.0f), color);
            else
                list.Rect(Bounds, color);
        }
    }

    /// <summary>Three blocks in a row, and a pill under them only while it's lit, the way a dropdown's row paints its hover.</summary>
    private sealed class Lit : UIComponent
    {
        public bool On { get; set; }

        protected override void Paint(UIDrawList list)
        {
            if (On)
                list.Rect(Bounds, Colour(99));
            for (int i = 0; i < 3; i++)
                list.Rect(new UIRect(Bounds.Min + new Vector2(i * 30.0f, 0.0f), Bounds.Min + new Vector2(i * 30.0f + 20.0f, 20.0f)), Colour(i));
        }
    }

    // Helper method for a colour nothing else is painted in, told apart by its red and green
    private static Vector4 Colour(int i) => new((i + 1) / 128.0f, 0.5f, 0.25f, 1.0f);

    private readonly record struct Painted(SpriteItem[] Items, UIDrawList.Run[] Runs, UIGlassPane[] Glass);

    // Helper method to lay out and paint one tick
    private static Painted Tick(UIComponent root, UIRect screen, int tick)
    {
        var list = new UIDrawList();
        root.MeasureTree(Skin);
        root.ArrangeTree(screen);
        list.Begin(Skin, tick, 1.0f / 120.0f, tick / 120.0f);
        root.PaintTree(list);
        list.End();
        return new Painted(list.Items.ToArray(), list.Runs.ToArray(), list.GlassPanes.ToArray());
    }

    // Helper method for the frame halfway between two ticks
    private static (SpriteItem[] Items, UIGlassPane[] Glass) Halfway(Painted a, Painted b)
    {
        var between = new UIInBetween();
        SpriteItem[] items = between.Quads(a.Items, a.Runs, b.Items, b.Runs, 0.5f).ToArray();
        UIGlassPane[] glass = between.Glass(a.Glass, b.Glass, 0.5f).ToArray();
        return (items, glass);
    }

    // Helper method for where the quads painted in a colour are on screen, all of them together
    private static UIRect Drawn(SpriteItem[] items, Vector4 colour)
    {
        uint wanted = SpriteItem.PackColor(colour) & 0x00FFFFFF;
        Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (SpriteItem item in items)
        {
            if ((item.Color & 0x00FFFFFF) != wanted)
                continue;
            Vector2 far = item.Origin + item.AxisX + item.AxisY;
            min = Vector2.Min(min, Vector2.Min(item.Origin, far));
            max = Vector2.Max(max, Vector2.Max(item.Origin, far));
        }

        Assert.True(min.X <= max.X, $"nothing was drawn in {colour}");
        return new UIRect(min, max);
    }

    // Helper method for a panel of ten rows twenty high that shows five of them, scrolled by so much
    private static ScrollPanel Rows(bool textured, out UIRect screen)
    {
        screen = new UIRect(Vector2.Zero, new Vector2(200.0f, 100.0f));
        var panel = new ScrollPanel { Fade = 0.0f };
        var stack = panel.Add(new StackPanel { Direction = UIDirection.Vertical, Spacing = 0.0f });
        for (int i = 0; i < 10; i++)
            stack.Add(new Swatch(Colour(i), textured));
        return panel;
    }

    [Fact(Skip = STAGE_2)]
    public void A_picture_cut_by_the_edge_of_a_scroll_moves_with_the_rest()
    {
        var panel = Rows(textured: true, out UIRect screen);
        Painted a = Tick(panel, screen, 1);
        panel.Offset = 10.0f;
        Painted b = Tick(panel, screen, 2);
        var (items, _) = Halfway(a, b);

        // The second row is whole both times and the first is cut by the top, both moved half of the ten
        Assert.Equal(Drawn(a.Items, Colour(1)).Min.Y + 5.0f, Drawn(items, Colour(1)).Min.Y, 0.01f);
        Assert.Equal(Drawn(a.Items, Colour(0)).Min.Y + 5.0f, Drawn(items, Colour(0)).Min.Y, 0.01f);
    }

    [Fact(Skip = STAGE_2)]
    public void A_row_coming_into_sight_comes_in_with_the_rest()
    {
        var panel = Rows(textured: false, out UIRect screen);
        Painted a = Tick(panel, screen, 1);
        panel.Offset = 10.0f;
        Painted b = Tick(panel, screen, 2);
        var (items, _) = Halfway(a, b);

        // The sixth row was all under the bottom and is ten in sight after, halfway it's five
        Assert.Equal(5.0f, Drawn(items, Colour(5)).Height, 0.01f);
    }

    [Fact(Skip = STAGE_2)]
    public void Something_lighting_up_doesnt_move_what_was_already_there()
    {
        var lit = new Lit();
        var screen = new UIRect(Vector2.Zero, new Vector2(100.0f, 20.0f));
        Painted a = Tick(lit, screen, 1);
        lit.On = true;
        Painted b = Tick(lit, screen, 2);
        var (items, _) = Halfway(a, b);

        for (int i = 0; i < 3; i++)
            Assert.Equal(Drawn(b.Items, Colour(i)), Drawn(items, Colour(i)));
    }

    [Fact(Skip = STAGE_2)]
    public void Glass_coming_in_doesnt_stop_the_glass_already_there_moving()
    {
        var panel = Rows(textured: false, out UIRect screen);
        var rows = panel.Children[0].Children;
        rows[1].Glass = UIGlass.Frosted;
        Painted a = Tick(panel, screen, 1);
        panel.Offset = 10.0f;
        rows[3].Glass = UIGlass.Frosted;
        Painted b = Tick(panel, screen, 2);
        var (_, glass) = Halfway(a, b);

        // The pane of the second row went up ten, halfway it's gone up five
        float was = a.Glass.Single().Min.Y;
        Assert.Contains(glass, pane => MathF.Abs(pane.Min.Y - (was + 5.0f)) < 0.01f);
    }

    [Fact(Skip = STAGE_2)]
    public void A_list_gliding_to_a_row_has_its_rows_where_its_frame_is()
    {
        var list = new ListBox { Rows = 10, Items = [.. Enumerable.Range(0, 40).Select(i => $"item {i}")] };
        var screen = new UIRect(Vector2.Zero, new Vector2(200.0f, 400.0f));
        Vector4 picked = Skin.TextColor;
        list.Choose(10);
        list.UpdateTree(1.0f / 120.0f);
        Painted a = Tick(list, screen, 1);
        list.UpdateTree(1.0f / 120.0f);
        Painted b = Tick(list, screen, 2);
        var (items, _) = Halfway(a, b);

        // The picked row comes in at the bottom, its top halfway between where the two ticks had it. Only the rows, the
        // bar down the right is the text's colour as well
        static SpriteItem[] Rows(SpriteItem[] items) => [.. items.Where(item => item.Origin.X < 150.0f)];
        float top = (Drawn(Rows(a.Items), picked).Max.Y + Drawn(Rows(b.Items), picked).Max.Y) * 0.5f;
        Assert.Equal(top, Drawn(Rows(items), picked).Max.Y, 0.01f);
    }

    [Fact]
    public void Another_popup_or_tooltip_comes_in_rather_than_the_last_one_sliding_over()
    {
        // Two lists dropped down by two components, then a tooltip of one text and then of another, each pair one
        // tick after the other in the same corner of the screen
        Painted Box(object owner, Vector2 at, int tick)
        {
            var list = new UIDrawList();
            list.Begin(Skin, tick, 1.0f / 120.0f, tick / 120.0f);
            list.PushOwner(owner);
            list.Rect(new UIRect(at, at + new Vector2(40.0f, 20.0f)), Colour(tick));
            list.PopOwner();
            list.End();
            return new Painted(list.Items.ToArray(), list.Runs.ToArray(), list.GlassPanes.ToArray());
        }

        foreach (var (first, second) in new (object, object)[] { (new Swatch(Colour(0)), new Swatch(Colour(0))), (new string('a', 3), new string('b', 3)) })
        {
            var (items, _) = Halfway(Box(first, Vector2.Zero, 1), Box(second, new Vector2(60.0f, 0.0f), 2));
            Assert.Equal(new Vector2(60.0f, 0.0f), Drawn(items, Colour(2)).Min);
        }
    }

    [Fact(Skip = STAGE_3)]
    public void Something_put_somewhere_else_by_the_layout_is_put_there()
    {
        var swatch = new Swatch(Colour(0));
        Painted a = Tick(swatch, new UIRect(Vector2.Zero, new Vector2(20.0f)), 1);
        Painted b = Tick(swatch, new UIRect(new Vector2(100.0f, 0.0f), new Vector2(120.0f, 20.0f)), 2);
        var (items, _) = Halfway(a, b);

        Assert.Equal(100.0f, Drawn(items, Colour(0)).Min.X, 0.01f);
    }
}
