using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests;

// A component that slides in and out like a drawer (UIComponent.Sliding), the room it takes going with it.
public class UISlideTests
{
    private const float STEP = 1.0f / 120.0f;

    // A box of a size, which needs no skin to be measured
    private sealed class Block : UIComponent;

    // A row of a side bar 100 wide that slides to the left and a middle 200 wide, laid out the way a compositor would
    private static (StackPanel Row, Panel Side, Block Inside, Block Middle) Made()
    {
        var row = new StackPanel { Direction = UIDirection.Horizontal, Spacing = 0.0f };
        var side = row.Add(new Panel { Slide = UISlide.Left });
        var inside = side.Add(new Block { Size = new Vector2(100, 50) });
        var middle = row.Add(new Block { Size = new Vector2(200, 50) });
        Lay(row);
        return (row, side, inside, middle);
    }

    private static void Lay(UIComponent row)
    {
        row.MeasureTree(null!);
        row.ArrangeTree(UIRect.FromCenter(Vector2.Zero, row.DesiredSize));
    }

    private static void Tick(UIComponent row, float seconds)
    {
        for (float t = 0.0f; t < seconds; t += STEP)
        {
            row.UpdateTree(STEP);
            Lay(row);
        }
    }

    [Fact]
    public void Put_away_it_gives_its_room_up_as_it_goes_and_is_hidden_at_the_end()
    {
        var (row, side, inside, middle) = Made();

        side.Show(false);
        Tick(row, UIComponent.DEFAULT_SLIDE_TIME * 0.4f);

        Assert.True(side.Visible && side.Sliding && !side.Shown);
        Assert.InRange(side.Bounds.Width, 1.0f, 99.0f);
        Assert.Equal(side.Bounds.Max.X, middle.Bounds.Min.X, 0.01f);

        // What's in it is its whole size, the part that's gone hanging off the left
        Assert.Equal(100.0f, inside.Bounds.Width, 0.01f);
        Assert.Equal(side.Bounds.Max.X, inside.Bounds.Max.X, 0.01f);

        Tick(row, UIComponent.DEFAULT_SLIDE_TIME);
        Assert.False(side.Visible || side.Sliding);
        Assert.Equal(200.0f, row.DesiredSize.X, 0.01f);
    }

    [Fact]
    public void Shown_again_it_comes_in_from_nothing()
    {
        var (row, side, _, _) = Made();
        side.Show(false, instantly: true);
        Lay(row);

        side.Show(true);
        Assert.True(side.Visible && side.Shown);
        Tick(row, STEP);
        Assert.InRange(side.Bounds.Width, 0.0f, 50.0f);

        Tick(row, UIComponent.DEFAULT_SLIDE_TIME);
        Assert.False(side.Sliding);
        Assert.Equal(100.0f, side.Bounds.Width, 0.01f);
    }

    [Fact]
    public void Without_a_way_to_slide_it_is_there_or_it_isnt()
    {
        var (row, side, _, _) = Made();
        side.Slide = UISlide.None;

        side.Show(false);
        Assert.False(side.Visible || side.Sliding);

        side.ToggleShown();
        Assert.True(side.Visible && side.Shown);
    }

    [Fact]
    public void A_panel_going_down_keeps_its_height_inside_and_shows_its_top_last()
    {
        var column = new StackPanel { Spacing = 0.0f };
        var top = column.Add(new Block { Size = new Vector2(100, 200) });
        var panel = column.Add(new Panel { Slide = UISlide.Down });
        var page = panel.Add(new Block { Size = new Vector2(100, 80) });
        Lay(column);

        panel.Show(false);
        Tick(column, UIComponent.DEFAULT_SLIDE_TIME * 0.4f);

        Assert.InRange(panel.Bounds.Height, 1.0f, 79.0f);
        Assert.Equal(80.0f, page.Bounds.Height, 0.01f);
        Assert.Equal(panel.Bounds.Max.Y, page.Bounds.Max.Y, 0.01f);
    }
}
