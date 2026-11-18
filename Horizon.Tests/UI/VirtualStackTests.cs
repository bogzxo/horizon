using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// The list that makes components only for the rows in sight. Few components for many rows, the row at the top staying
/// put as heights come in other than estimated, sticking to the end as rows come, and components used again by kind.
/// </summary>
public class VirtualStackTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));

    private static readonly UIRect View = UIRect.FromCenter(Vector2.Zero, new Vector2(300, 200));

    private static void Lay(VirtualStack stack)
    {
        stack.MeasureTree(Skin);
        stack.ArrangeTree(View);
    }

    private static VirtualStack Rows(int count, float height, float estimate, List<Panel>? made = null) => new()
    {
        ItemCount = count,
        Estimate = estimate,
        Padding = new UIEdges(0),
        Make = _ =>
        {
            var row = new Panel { Size = new Vector2(0, height) };
            made?.Add(row);
            return row;
        }
    };

    [Fact]
    public void Only_the_rows_near_the_view_have_components()
    {
        var made = new List<Panel>();
        VirtualStack stack = Rows(1000, 20, 20, made);
        Lay(stack);

        Assert.Equal(20000, stack.ContentHeight, 1);
        Assert.Equal(0, stack.Realized.First);
        Assert.InRange(stack.Realized.Last, 9, 25);
        Assert.InRange(stack.Children.Count, 10, 26);

        stack.Offset = 10000;
        Lay(stack);
        Assert.InRange(stack.Realized.First, 485, 495);
        Assert.InRange(stack.Realized.Last, 510, 525);
        Assert.InRange(stack.Children.Count, 20, 45);
        Assert.InRange(made.Count, 20, 80);

        // The row at the offset is at the top of the view
        UIComponent top = stack.ComponentOf(500)!;
        Assert.Equal(View.Max.Y, top.Bounds.Max.Y, 1);
        Assert.Equal(20, top.Bounds.Height, 1);
    }

    [Fact]
    public void The_row_at_the_top_stays_put_as_heights_come_in()
    {
        // Estimated at 10, every row is 30 once it's made
        VirtualStack stack = Rows(1000, 30, 10);
        Lay(stack);
        stack.Offset = 3000;
        int anchor = 0;
        while (stack.TopOf(anchor + 1) <= 3000)
            anchor++;
        float into = 3000 - stack.TopOf(anchor);
        Lay(stack);

        // The rows round it measured three times their estimate, and it's where it was on screen
        Assert.Equal(stack.TopOf(anchor) + into, stack.Offset, 1);
        Assert.Equal(View.Max.Y + into, stack.ComponentOf(anchor)!.Bounds.Max.Y, 1);
        Assert.Equal(30, stack.ComponentOf(anchor)!.Bounds.Height, 1);

        // Laid out again, nothing moves
        float offset = stack.Offset;
        Lay(stack);
        Assert.Equal(offset, stack.Offset, 1);
    }

    [Fact]
    public void At_its_end_it_stays_there_as_rows_come()
    {
        VirtualStack stack = Rows(100, 20, 20);
        stack.StickToEnd = true;
        Lay(stack);
        stack.ScrollTo(99);
        stack.Offset = float.MaxValue;
        Lay(stack);
        Assert.Equal(2000 - 200, stack.Offset, 1);

        stack.ItemCount = 150;
        stack.ItemsChanged();
        Lay(stack);
        Assert.Equal(3000 - 200, stack.Offset, 1);
        Assert.Equal(149, stack.Realized.Last);
        Assert.Equal(View.Min.Y, stack.ComponentOf(149)!.Bounds.Min.Y, 1);

        // Scrolled up, it stays where it was put
        stack.Offset = 100;
        stack.ItemCount = 160;
        stack.ItemsChanged();
        Lay(stack);
        Assert.Equal(100, stack.Offset, 1);
    }

    [Fact]
    public void Components_are_used_again_for_rows_of_their_kind()
    {
        int made = 0, bound = 0, recycled = 0;
        var stack = new VirtualStack
        {
            ItemCount = 5000,
            Estimate = 20,
            Padding = new UIEdges(0),
            KindOf = index => index % 2,
            Make = _ =>
            {
                made++;
                return new Panel { Size = new Vector2(0, 20) };
            },
            Bind = (_, _) => bound++,
            Recycle = (_, _) => recycled++
        };
        Lay(stack);
        for (int step = 1; step <= 50; step++)
        {
            stack.Offset = step * 1000;
            Lay(stack);
        }

        Assert.True(recycled > 0);
        Assert.True(bound > made, $"made {made}, bound {bound}");
        Assert.InRange(stack.Children.Count, 20, 45);
    }

    [Fact]
    public void A_row_that_grew_is_taken_at_its_new_height()
    {
        Panel? last = null;
        var stack = new VirtualStack
        {
            ItemCount = 10,
            Estimate = 20,
            Padding = new UIEdges(0),
            Make = index =>
            {
                var row = new Panel { Size = new Vector2(0, 20) };
                if (index == 9)
                    last = row;
                return row;
            }
        };
        Lay(stack);
        Assert.Equal(200, stack.ContentHeight, 1);

        last!.Size = new Vector2(0, 120);
        stack.ItemGrew(9);
        Lay(stack);
        Assert.Equal(300, stack.ContentHeight, 1);
        Assert.Equal(120, last.Bounds.Height, 1);
    }

    [Fact]
    public void A_hidden_row_takes_no_room_and_no_gap()
    {
        var stack = new VirtualStack
        {
            ItemCount = 10,
            Estimate = 20,
            Spacing = 8,
            Padding = new UIEdges(0),
            Make = index => new Panel { Size = new Vector2(0, 20), Visible = index % 2 == 0 }
        };
        Lay(stack);

        // Five rows of 20 with four gaps between them, as a stack leaves out what isn't visible
        Assert.Equal(5 * 20 + 4 * 8, stack.ContentHeight, 1);
    }

    [Fact]
    public void A_row_given_another_component_is_shown_with_it()
    {
        var rows = Enumerable.Range(0, 20).Select(_ => (UIComponent)new Panel { Size = new Vector2(0, 20) }).ToList();
        var stack = new VirtualStack { ItemCount = 20, Estimate = 20, Padding = new UIEdges(0), Make = index => rows[index] };
        Lay(stack);
        UIComponent was = rows[3];
        rows[3] = new Panel { Size = new Vector2(0, 50) };
        stack.Refresh(3);
        Lay(stack);

        Assert.Same(rows[3], stack.ComponentOf(3));
        Assert.DoesNotContain(was, stack.Children);
        Assert.Equal(19 * 20 + 50, stack.ContentHeight, 1);

        // Every row asked for again keeps the heights it had
        stack.Rebind();
        Assert.Empty(stack.Children);
        Assert.Equal(19 * 20 + 50, stack.ContentHeight, 1);
        Lay(stack);
        Assert.Same(rows[0], stack.ComponentOf(0));
    }

    [Fact]
    public void A_reset_lets_every_component_go()
    {
        int recycled = 0;
        VirtualStack stack = Rows(50, 20, 20);
        stack.Recycle = (_, _) => recycled++;
        Lay(stack);
        int had = stack.Children.Count;
        stack.Reset();
        Assert.Empty(stack.Children);
        Assert.Equal(had, recycled);
        Assert.Equal((0, -1), stack.Realized);
    }

    // The row at the top of the view, by where the rows are now
    private static int TopRow(VirtualStack stack)
    {
        int row = 0;
        while (row < stack.ItemCount - 1 && stack.TopOf(row + 1) <= stack.Offset)
            row++;
        return row;
    }

    [Fact]
    public void The_wheel_goes_as_far_as_it_was_turned_through_rows_not_yet_measured()
    {
        // Estimated at 10, every row is 30 once it's made, the way a chat just opened is
        VirtualStack stack = Rows(1000, 30, 10);
        Lay(stack);
        stack.Offset = 6000;
        Lay(stack);
        int from = TopRow(stack);

        // Thirty notches up is 1440 pixels, 48 rows of 30, however much the rows above measure on the way
        stack.Wheel(30);
        for (int tick = 0; tick < 240; tick++)
        {
            stack.UpdateTree(1.0f / 60.0f);
            Lay(stack);
        }

        Assert.InRange(from - TopRow(stack), 46, 50);
    }
}
