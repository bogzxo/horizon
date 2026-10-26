using System.Numerics;

using Horizon.Core;

namespace Horizon.Tests;

/// <summary>Which edges of a frameless window a point resizes, the edges as thick as the system's and the corners reaching further.</summary>
public class WindowEdgesTests
{
    private static readonly Vector2 Size = new(1200, 800);

    private static WindowEdges At(float x, float y, float scale = 1.0f) => WindowManager.EdgesAt(new Vector2(x, y), Size, scale);

    [Fact]
    public void Each_edge_is_a_few_pixels_in_and_the_inside_is_no_edge()
    {
        Assert.Equal(WindowEdges.Left, At(3, 400));
        Assert.Equal(WindowEdges.Right, At(1196, 400));
        Assert.Equal(WindowEdges.Top, At(600, 2));
        Assert.Equal(WindowEdges.Bottom, At(600, 797));
        Assert.Equal(WindowEdges.None, At(20, 400));
        Assert.Equal(WindowEdges.None, At(600, 400));
    }

    [Fact]
    public void A_corner_reaches_along_both_of_its_edges()
    {
        Assert.Equal(WindowEdges.Left | WindowEdges.Top, At(1, 1));
        Assert.Equal(WindowEdges.Left | WindowEdges.Top, At(3, 15));
        Assert.Equal(WindowEdges.Left | WindowEdges.Top, At(15, 3));
        Assert.Equal(WindowEdges.Right | WindowEdges.Bottom, At(1185, 797));
        Assert.Equal(WindowEdges.Right | WindowEdges.Top, At(1197, 10));

        // Past the corner's reach it's the edge again
        Assert.Equal(WindowEdges.Left, At(3, 30));
    }

    [Fact]
    public void A_scaled_screen_has_thicker_edges_and_off_the_window_is_nothing()
    {
        Assert.Equal(WindowEdges.None, At(10, 400));
        Assert.Equal(WindowEdges.Left, At(10, 400, 1.5f));
        Assert.Equal(WindowEdges.None, At(-1, 400));
        Assert.Equal(WindowEdges.None, At(600, 801));
    }
}
