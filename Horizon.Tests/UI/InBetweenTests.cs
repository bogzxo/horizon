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
/// is one of the bugs in notes/ui-interpolation-plan.md.
/// </summary>
public class InBetweenTests
{
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

    private readonly record struct Painted(SpriteItem[] Items, UIDrawList.Run[] Runs, UIGlassPane[] Glass, UIFrameList Frames);

    // Helper method to keep what a list painted, its frames as well
    private static Painted Kept(UIDrawList list)
    {
        var frames = new UIFrameList();
        frames.CopyFrom(list.FrameList);
        return new Painted(list.Items.ToArray(), list.Runs.ToArray(), list.GlassPanes.ToArray(), frames);
    }

    // Helper method to lay out and paint one tick, while the layout is moving things about or not
    private static Painted Tick(UIComponent root, UIRect screen, int tick, bool layoutMoves = false)
    {
        var list = new UIDrawList();
        root.MeasureTree(Skin);
        root.ArrangeTree(screen);
        list.Begin(Skin, tick, 1.0f / 120.0f, tick / 120.0f);
        list.LayoutMoves = layoutMoves;
        root.PaintTree(list);
        list.End();
        return Kept(list);
    }

    // Helper method for the frame halfway between two ticks, the way the compositor puts it together
    private static (SpriteItem[] Items, UIGlassPane[] Glass) Halfway(Painted a, Painted b)
    {
        var between = new UIInBetween();
        between.Frames(a.Frames, b.Frames, 0.5f);
        return (between.FramedQuads.ToArray(), between.FramedGlass.ToArray());
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public void A_list_gliding_to_a_row_has_its_rows_where_its_frame_is()
    {
        var list = new ListBox { Rows = 10, Items = [.. Enumerable.Range(0, 40).Select(i => $"item {i}")] };
        var screen = new UIRect(Vector2.Zero, new Vector2(200.0f, 400.0f));
        Vector4 picked = Skin.TextColor;
        list.Choose(10);
        list.UpdateTree(1.0f / 120.0f);
        Painted a = Tick(list, screen, 1);
        for (int i = 0; i < 20; i++)
            list.UpdateTree(1.0f / 120.0f);
        Painted b = Tick(list, screen, 2);
        var (items, _) = Halfway(a, b);
        static SpriteItem[] Rows(SpriteItem[] items) => [.. items.Where(item => item.Origin.X < 150.0f)];
        Assert.True(Drawn(Rows(b.Items), picked).Max.Y - Drawn(Rows(a.Items), picked).Max.Y > 2.0f, "the list hardly moved");

        // The picked row comes in at the bottom, its top halfway between where the two ticks had it, on a whole pixel
        // from where the newer one has it. Only the rows, the bar down the right is the text's colour as well
        float newer = Drawn(Rows(b.Items), picked).Max.Y, drawn = Drawn(Rows(items), picked).Max.Y;
        float top = (Drawn(Rows(a.Items), picked).Max.Y + newer) * 0.5f;
        Assert.InRange(drawn, top - 0.5f, top + 0.5f);
        Assert.Equal(MathF.Round(drawn - newer), drawn - newer, 0.001f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_list_stays_continuous_as_edge_bounce_starts_and_settles(bool bottom)
    {
        var list = new ListBox
        {
            Rows = 10,
            Headings = ["Name", "Size", "Modified"],
            Items = [.. Enumerable.Range(0, 40).Select(i => $"file-{i}.cs")]
        };
        var screen = new UIRect(Vector2.Zero, new Vector2(300, 440));
        if (bottom)
        {
            list.Choose(39);
            for (int i = 0; i < 120; i++) list.UpdateTree(1.0f / 120.0f);
        }
        // Track a selected row away from either clip edge, excluding the scrollbar's matching ink.
        list.Choose(bottom ? 35 : 4);
        static SpriteItem[] Rows(SpriteItem[] items) => [.. items.Where(item => item.Origin.X < 250)];
        Painted before = Tick(list, screen, 1);
        float original = Drawn(Rows(before.Items), Skin.TextColor).Max.Y;
        Assert.True(list.OnScroll(bottom ? -1 : 1));
        bool moved = false;
        for (int tick = 2; tick <= 180; tick++)
        {
            list.UpdateTree(1.0f / 120.0f);
            Painted after = Tick(list, screen, tick);
            float was = Drawn(Rows(before.Items), Skin.TextColor).Max.Y;
            float now = Drawn(Rows(after.Items), Skin.TextColor).Max.Y;
            moved |= MathF.Abs(now - original) > 1;
            var (items, _) = Halfway(before, after);
            float drawn = Drawn(Rows(items), Skin.TextColor).Max.Y;
            float expected = (was + now) * 0.5f;
            Assert.True(MathF.Abs(drawn - expected) <= 0.51f,
                $"tick {tick}: row jumped to {drawn}, expected {expected} between {was} and {now}");
            before = after;
        }
        Assert.True(moved, "the edge bounce was never exercised");
        Assert.Equal(original, Drawn(Rows(before.Items), Skin.TextColor).Max.Y, 0.001f);
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
            return Kept(list);
        }

        foreach (var (first, second) in new (object, object)[] { (new Swatch(Colour(0)), new Swatch(Colour(0))), (new string('a', 3), new string('b', 3)) })
        {
            var (items, _) = Halfway(Box(first, Vector2.Zero, 1), Box(second, new Vector2(60.0f, 0.0f), 2));
            Assert.Equal(new Vector2(60.0f, 0.0f), Drawn(items, Colour(2)).Min);
        }
    }

    [Fact]
    public void Something_put_somewhere_else_by_the_layout_is_put_there()
    {
        var swatch = new Swatch(Colour(0));
        Painted a = Tick(swatch, new UIRect(Vector2.Zero, new Vector2(20.0f)), 1);
        Painted b = Tick(swatch, new UIRect(new Vector2(100.0f, 0.0f), new Vector2(120.0f, 20.0f)), 2);
        var (items, _) = Halfway(a, b);

        Assert.Equal(100.0f, Drawn(items, Colour(0)).Min.X, 0.01f);
    }

    [Fact]
    public void What_the_layout_moves_while_something_slides_moves_smoothly()
    {
        // Pushed along by a drawer coming out next to it, twenty to the right in a tick
        var swatch = new Swatch(Colour(0));
        Painted a = Tick(swatch, new UIRect(Vector2.Zero, new Vector2(20.0f)), 1, layoutMoves: true);
        Painted b = Tick(swatch, new UIRect(new Vector2(20.0f, 0.0f), new Vector2(40.0f, 20.0f)), 2, layoutMoves: true);
        var (items, _) = Halfway(a, b);

        Assert.Equal(10.0f, Drawn(items, Colour(0)).Min.X, 0.01f);
    }

    [Fact]
    public void A_window_being_dragged_is_where_the_pointer_is_now()
    {
        // Painted where the pointer was as the tick laid it out, drawn where the pointer has got to since
        var list = new UIDrawList();
        list.Begin(Skin, 1, 1.0f / 120.0f, 1.0f / 120.0f);
        list.PushFollowFrame(new Vector2(100.0f, 100.0f));
        list.Rect(new UIRect(new Vector2(90.0f), new Vector2(110.0f)), Colour(0));
        list.PopFollowFrame();
        list.Rect(new UIRect(Vector2.Zero, new Vector2(10.0f)), Colour(1));
        list.End();
        Painted painted = Kept(list);

        var between = new UIInBetween();
        between.Frames(painted.Frames, painted.Frames, 1.0f, new Vector2(130.4f, 95.0f));
        SpriteItem[] items = between.FramedQuads.ToArray();
        Assert.Equal(new Vector2(120.0f, 85.0f), Drawn(items, Colour(0)).Min);
        Assert.Equal(Vector2.Zero, Drawn(items, Colour(1)).Min);
    }

    [Fact]
    public void A_stale_drag_frame_does_not_follow_motion_after_release_before_the_next_tick()
    {
        var mouse = new Horizon.Input.Mouse();
        mouse.Place(new Vector2(100));
        mouse.HearButton(Horizon.Input.MouseButton.Left, true);
        mouse.Update();
        var list = new UIDrawList();
        list.Begin(Skin, 1, 1.0f / 120, 1.0f / 120);
        list.PushFollowFrame(new Vector2(100));
        list.Rect(new UIRect(new Vector2(90), new Vector2(110)), Colour(0));
        list.PopFollowFrame();
        list.End();
        Painted painted = Kept(list);
        var between = new UIInBetween();

        mouse.Place(new Vector2(130, 95));
        between.Frames(painted.Frames, painted.Frames, 1, UICompositor.LiveDragPointer(mouse));
        Assert.Equal(new Vector2(120, 85), Drawn(between.FramedQuads.ToArray(), Colour(0)).Min);

        mouse.HearButton(Horizon.Input.MouseButton.Left, false);
        mouse.Place(new Vector2(500, 700));
        Assert.True(mouse.IsDown(Horizon.Input.MouseButton.Left)); // Simulation has not heard release yet.
        Assert.Null(UICompositor.LiveDragPointer(mouse));
        between.Frames(painted.Frames, painted.Frames, 1, UICompositor.LiveDragPointer(mouse));
        Assert.Equal(new Vector2(90), Drawn(between.FramedQuads.ToArray(), Colour(0)).Min);
        mouse.Update();
        Assert.False(mouse.IsDown(Horizon.Input.MouseButton.Left));
    }

    [Fact]
    public void Something_fading_in_is_half_there_halfway()
    {
        var swatch = new Swatch(Colour(0)) { Opacity = 0.2f };
        var screen = new UIRect(Vector2.Zero, new Vector2(20.0f));
        Painted a = Tick(swatch, screen, 1);
        swatch.Opacity = 0.6f;
        Painted b = Tick(swatch, screen, 2);
        var (items, _) = Halfway(a, b);

        uint wanted = SpriteItem.PackColor(Colour(0)) & 0x00FFFFFF;
        SpriteItem drawn = items.Single(item => (item.Color & 0x00FFFFFF) == wanted);
        Assert.InRange((drawn.Color >> 24) / 255.0f, 0.39f, 0.41f);
    }
}
