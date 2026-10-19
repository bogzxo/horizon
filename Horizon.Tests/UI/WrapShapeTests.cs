using System.Numerics;

using Horizon.UI;

namespace Horizon.Tests;

public class WrapShapeTests
{
    // A 10 by 4 picture with something drawn in columns 6 to 8 of the top two rows and nothing below
    private static UIWrapShape Corner()
    {
        var pixels = new byte[10 * 4 * 4];
        for (int y = 0; y < 2; y++)
        for (int x = 6; x <= 8; x++)
            pixels[(y * 10 + x) * 4 + 3] = 255;

        // A pixel too faint to count
        pixels[(3 * 10 + 1) * 4 + 3] = 10;
        return UIWrapShape.FromPixels(pixels, 10, 4);
    }

    [Fact]
    public void The_shape_is_where_the_picture_has_something_and_not_where_it_is_see_through()
    {
        var shape = Corner();
        var placed = new UIRect(new Vector2(100, 0), new Vector2(200, 40));

        // The top half of the box is the top two rows, columns 6 to 8 are 160 to 190, to the pixel
        Assert.True(shape.Across(placed, 25, 35, out float from, out float to));
        Assert.Equal(160, from, 0.01f);
        Assert.Equal(190, to, 0.01f);

        // The bottom half has nothing, the faint pixel included
        Assert.False(shape.Across(placed, 2, 15, out _, out _));

        // Nor does anything outside of the box
        Assert.False(shape.Across(placed, 50, 60, out _, out _));
    }

    [Fact]
    public void The_margin_is_round_all_of_it_level_with_the_shape_and_less_further_off()
    {
        var shape = UIWrapShape.Box();
        var placed = new UIRect(new Vector2(100, 100), new Vector2(200, 200));
        const float MARGIN = 10.0f;

        // Level with it, the whole margin either side
        Assert.True(shape.Across(placed, 140, 160, out float from, out float to, MARGIN));
        Assert.Equal(90, from, 0.01f);
        Assert.Equal(210, to, 0.01f);

        // Six over the top of it, eight of the margin out to the side (a three, four, five triangle of it)
        Assert.True(shape.Across(placed, 206, 216, out from, out to, MARGIN));
        Assert.Equal(92, from, 0.01f);
        Assert.Equal(208, to, 0.01f);

        // Further off than the margin, out of reach, and without one nothing over the top counts
        Assert.False(shape.Across(placed, 211, 220, out _, out _, MARGIN));
        Assert.False(shape.Across(placed, 206, 216, out _, out _));
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
