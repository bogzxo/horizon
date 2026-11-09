using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// The sums of the overlay a rectangle is moved and sized by hand with, which handle is where and what a pull on one
/// does to the rectangle, its shape kept or let go of. No window anywhere near.
/// </summary>
public class ArrangeOverlayTests
{
    // 200 across and 100 up, its bottom left corner at 100, 100 (y goes up)
    private static readonly UIRect Box = new(new Vector2(100, 100), new Vector2(300, 200));

    private static void Same(UIRect expected, UIRect actual)
    {
        Assert.Equal(expected.Min.X, actual.Min.X, 0.01f);
        Assert.Equal(expected.Min.Y, actual.Min.Y, 0.01f);
        Assert.Equal(expected.Max.X, actual.Max.X, 0.01f);
        Assert.Equal(expected.Max.Y, actual.Max.Y, 0.01f);
    }

    [Fact]
    public void The_handles_are_on_the_corners_and_the_middles_of_the_edges()
    {
        var overlay = new ArrangeOverlay { Rect = Box };

        Assert.Equal(UIResize.Left | UIResize.Bottom, overlay.HandleAt(new Vector2(101, 101)));
        Assert.Equal(UIResize.Right | UIResize.Top, overlay.HandleAt(new Vector2(305, 204)));
        Assert.Equal(UIResize.Top, overlay.HandleAt(new Vector2(200, 198)));
        Assert.Equal(UIResize.Left, overlay.HandleAt(new Vector2(96, 150)));

        // The middle moves the whole thing, and so does an edge away from its handle
        Assert.Equal(UIResize.None, overlay.HandleAt(new Vector2(200, 150)));
        Assert.Equal(UIResize.None, overlay.HandleAt(new Vector2(150, 199)));
        Assert.Equal(UIResize.None, overlay.HandleAt(new Vector2(500, 500)));
    }

    [Fact]
    public void A_corner_keeps_the_shape_and_the_opposite_corner_stays_put()
    {
        // The top right corner pulled 100 across and 10 up, across wins and the height follows
        UIRect pulled = ArrangeOverlay.Resized(Box, UIResize.Right | UIResize.Top, new Vector2(100, 10), keepShape: true);
        Same(new UIRect(new Vector2(100, 100), new Vector2(400, 250)), pulled);

        // The bottom left corner the other way, up is the one pulled further this time
        pulled = ArrangeOverlay.Resized(Box, UIResize.Left | UIResize.Bottom, new Vector2(-10, -100), keepShape: true);
        Same(new UIRect(new Vector2(-100, 0), new Vector2(300, 200)), pulled);
    }

    [Fact]
    public void An_edge_brings_the_other_way_along_round_its_middle()
    {
        UIRect pulled = ArrangeOverlay.Resized(Box, UIResize.Right, new Vector2(200, 0), keepShape: true);
        Same(new UIRect(new Vector2(100, 50), new Vector2(500, 250)), pulled);
    }

    [Fact]
    public void Its_shape_let_go_of_an_edge_moves_on_its_own()
    {
        UIRect pulled = ArrangeOverlay.Resized(Box, UIResize.Right | UIResize.Top, new Vector2(100, 10), keepShape: false);
        Same(new UIRect(new Vector2(100, 100), new Vector2(400, 210)), pulled);
    }

    [Fact]
    public void It_never_gets_smaller_than_the_least()
    {
        UIRect pulled = ArrangeOverlay.Resized(Box, UIResize.Left, new Vector2(1000, 0), keepShape: false);
        Assert.Equal(ArrangeOverlay.LEAST, pulled.Width, 0.01f);
        Assert.Equal(300.0f, pulled.Max.X, 0.01f);

        pulled = ArrangeOverlay.Resized(Box, UIResize.Right | UIResize.Top, new Vector2(-1000, -1000), keepShape: true);
        Assert.True(pulled.Width >= ArrangeOverlay.LEAST - 0.01f && pulled.Height >= ArrangeOverlay.LEAST - 0.01f);
        Assert.Equal(2.0f, pulled.Width / pulled.Height, 0.01f);
    }
}
