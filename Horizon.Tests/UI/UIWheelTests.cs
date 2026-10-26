using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

// Where the wheel goes. A window over an editor has it over itself and nothing behind gets it, what a scroll panel cuts
// off doesn't take it from what's really under the pointer, and the bar of a scroll panel is dragged and clicked.
public class UIWheelTests
{
    // A box of a size, which needs no skin to be measured
    private sealed class Block : UIComponent;

    // A box the pointer stops at with no use for the wheel, a button with no skin to measure it by
    private sealed class Knob : UIComponent
    {
        protected override bool HitTestVisible => true;
    }

    // Something behind the window that wants every turn of the wheel, a code editor say
    private sealed class Wheelie : UIComponent
    {
        public int Turns;

        protected override bool HitTestVisible => true;

        protected internal override bool OnScroll(float delta)
        {
            Turns++;
            return true;
        }
    }

    // A screen 800 by 600 with an editor over all of it and a window 400 by 300 in the middle, a list in the window a
    // lot taller than it and a button over the list's top
    private static (Panel Root, Wheelie Editor, WindowPanel Window, ScrollPanel List, Knob Button) Made()
    {
        var root = new Panel();
        var editor = root.Add(new Wheelie { Fill = UIFill.Both });
        var window = root.Add(new WindowPanel("Settings") { Size = new Vector2(400, 300) });
        var list = window.Add(new ScrollPanel { Fill = UIFill.Both });
        list.Add(new Block { Size = new Vector2(100, 3000) });
        var button = window.Add(new Knob { Size = new Vector2(80, 24), Anchor = Horizon.Rendering.Origin.TopLeft, Pivot = Horizon.Rendering.Origin.TopLeft });

        root.MeasureTree(null!);
        root.ArrangeTree(UIRect.FromCenter(Vector2.Zero, new Vector2(800, 600)));
        return (root, editor, window, list, button);
    }

    private static UIComponent? Wheel(UIComponent root, Vector2 at) =>
        root.HitTest(at) is { } over ? UICompositor.GiveWheel(over, null, -1.0f) : null;

    private static void Settle(UIComponent root)
    {
        for (int i = 0; i < 120; i++)
            root.UpdateTree(1.0f / 60.0f);
    }

    [Fact]
    public void Over_the_list_in_the_window_the_list_scrolls_and_the_editor_behind_gets_nothing()
    {
        var (root, editor, _, list, _) = Made();

        Assert.Same(list, Wheel(root, list.Bounds.Center));
        Settle(root);
        Assert.True(list.Offset > 0.0f);
        Assert.Equal(0, editor.Turns);
    }

    [Fact]
    public void Over_a_part_of_the_window_with_no_use_for_it_the_wheel_goes_to_what_the_window_says_and_never_through()
    {
        var (root, editor, window, list, _) = Made();
        Vector2 title = window.TitleBounds.Center;

        // With nothing named it's swallowed
        Assert.Same(window, Wheel(root, title));
        Settle(root);
        Assert.Equal(0.0f, list.Offset);
        Assert.Equal(0, editor.Turns);

        // And named it goes down the list
        window.WheelTarget = list;
        Wheel(root, title);
        Settle(root);
        Assert.True(list.Offset > 0.0f);
        Assert.Equal(0, editor.Turns);
    }

    [Fact]
    public void The_list_running_on_past_the_bottom_of_its_window_doesnt_take_the_wheel_off_the_editor_down_there()
    {
        var (root, editor, window, list, _) = Made();
        Vector2 below = new(list.Bounds.Center.X, window.Bounds.Min.Y - 50.0f);

        Assert.Same(editor, Wheel(root, below));
        Assert.Equal(1, editor.Turns);
        Assert.Equal(0.0f, list.Offset);

        // What's drawn there is the editor too, not the list's content cut off above it
        Assert.Same(editor, UILayoutOverlay.FindUnder(root, below, false));
    }

    [Fact]
    public void An_open_list_that_cant_scroll_keeps_the_wheel_from_what_it_was_opened_from()
    {
        var (root, _, _, list, button) = Made();
        Assert.Null(UICompositor.GiveWheel(button, button, -1.0f));
        Settle(root);
        Assert.Equal(0.0f, list.Offset);
    }

    [Fact]
    public void The_thumb_dragged_keeps_where_it_was_taken_and_a_click_on_the_track_jumps_there()
    {
        var (_, _, _, list, _) = Made();
        UIRect thumb = list.ThumbBounds;
        Assert.False(thumb.IsEmpty);

        // Taken a little under its top and dragged down 40, its top goes down 40 and no more
        Vector2 grab = new(thumb.Center.X, thumb.Max.Y - 3.0f);
        list.OnPointerDown(grab);
        list.OnPointerDrag(grab - new Vector2(0.0f, 40.0f));
        list.OnPointerUp(grab);
        Assert.Equal(thumb.Max.Y - 40.0f, list.ThumbBounds.Max.Y, 0.01f);
        Assert.True(list.Offset > 0.0f);

        // A click on the track near the bottom puts the thumb's middle there
        UIRect track = list.TrackBounds;
        Vector2 low = new(track.Center.X, track.Min.Y + track.Height * 0.2f);
        list.OnPointerDown(low);
        list.OnPointerUp(low);
        Assert.Equal(low.Y, list.ThumbBounds.Center.Y, 0.01f);
    }

    [Fact]
    public void A_press_in_the_gap_before_the_bar_takes_it_and_one_further_in_doesnt()
    {
        var (_, _, _, list, _) = Made();
        UIRect track = list.TrackBounds;

        list.OnPointerDown(new Vector2(track.Min.X - list.BarGap * 0.5f, track.Min.Y + 10.0f));
        Assert.True(list.IsDraggingBar);
        list.OnPointerUp(default);

        list.Offset = 0.0f;
        list.OnPointerDown(new Vector2(track.Min.X - list.BarGap - 10.0f, track.Min.Y + 10.0f));
        Assert.False(list.IsDraggingBar);
        Assert.Equal(0.0f, list.Offset);
    }

    [Fact]
    public void A_thicker_bar_is_as_easy_to_take_hold_of_as_it_is_thick()
    {
        var (root, _, _, list, _) = Made();
        list.BarWidth = 12.0f;
        root.MeasureTree(null!);
        root.ArrangeTree(UIRect.FromCenter(Vector2.Zero, new Vector2(800, 600)));

        Assert.Equal(12.0f, list.TrackBounds.Width, 0.01f);
        list.OnPointerDown(new Vector2(list.TrackBounds.Min.X + 1.0f, list.TrackBounds.Min.Y + 10.0f));
        Assert.True(list.IsDraggingBar);
    }
}
