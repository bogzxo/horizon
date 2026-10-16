using System.Numerics;

using Horizon.UI;

namespace Horizon.Tests;

public class WrapShapeTests
{
    // A 10 by 4 picture with something drawn in columns 6 to 8 of the top two rows and nothing below
    private static UIWrapShape Corner(int grow)
    {
        var pixels = new byte[10 * 4 * 4];
        for (int y = 0; y < 2; y++)
        for (int x = 6; x <= 8; x++)
            pixels[(y * 10 + x) * 4 + 3] = 255;

        // A pixel too faint to count
        pixels[(3 * 10 + 1) * 4 + 3] = 10;
        return UIWrapShape.FromPixels(pixels, 10, 4, grow);
    }

    [Fact]
    public void The_shape_is_where_the_picture_has_something_and_not_where_it_is_see_through()
    {
        var shape = Corner(grow: 0);
        var placed = new UIRect(new Vector2(100, 0), new Vector2(200, 40));

        // The top half of the box is the top two rows, columns 6 to 8 are 160 to 190
        Assert.True(shape.Across(placed, 25, 35, out float from, out float to));
        Assert.Equal(160, from, 0.01f);
        Assert.Equal(190, to, 0.01f);

        // The bottom half has nothing, the faint pixel included
        Assert.False(shape.Across(placed, 2, 15, out _, out _));

        // Nor does anything outside of the box
        Assert.False(shape.Across(placed, 50, 60, out _, out _));
    }

    [Fact]
    public void Grown_it_reaches_the_rows_and_columns_next_to_it()
    {
        var shape = Corner(grow: 1);
        var placed = new UIRect(Vector2.Zero, new Vector2(10, 4));

        // Row 2 (from 1 to 2 up) is next to row 1, so it's in now, a column wider either way
        Assert.True(shape.Across(placed, 1.2f, 1.8f, out float from, out float to));
        Assert.Equal(5, from, 0.01f);
        Assert.Equal(10, to, 0.01f);
        Assert.False(shape.Across(placed, 0.2f, 0.8f, out _, out _));
    }

    [Fact]
    public void A_box_is_all_of_where_it_is_put()
    {
        var placed = new UIRect(new Vector2(3, 3), new Vector2(7, 9));
        Assert.True(UIWrapShape.Box().Across(placed, 4, 5, out float from, out float to));
        Assert.Equal(3, from);
        Assert.Equal(7, to);
    }
}
