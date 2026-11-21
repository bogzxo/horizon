using System.Drawing;
using System.Numerics;

using Horizon.Platform.Desktop;

namespace Horizon.Tests.Platform;

/// <summary>
/// What of a frameless window's title bar moves the window. The bar and what's on it are said in the window's pixels, and
/// a point SDL asks about is turned into those first: on a Retina Mac SDL asks in points, two pixels each way.
/// </summary>
public class TitleBarHitTests
{
    // A bar 1600 pixels across and 72 down, a menu at 40 to 200 and a close button at its right end
    private static readonly RectangleF Bar = RectangleF.FromLTRB(0, 0, 1600, 72);
    private static readonly RectangleF[] Holes = [RectangleF.FromLTRB(40, 0, 200, 72), RectangleF.FromLTRB(1500, 0, 1600, 72)];

    [Fact]
    public void The_bar_between_what_is_on_it_moves_the_window_and_the_rest_does_not()
    {
        Assert.True(DesktopWindow.OnBar(Bar, Holes, new Vector2(800, 30)));
        Assert.False(DesktopWindow.OnBar(Bar, Holes, new Vector2(100, 30)));
        Assert.False(DesktopWindow.OnBar(Bar, Holes, new Vector2(1550, 30)));
        Assert.False(DesktopWindow.OnBar(Bar, Holes, new Vector2(800, 100)));
        Assert.False(DesktopWindow.OnBar(RectangleF.Empty, [], new Vector2(0, 0)));
    }

    [Fact]
    public void A_point_of_a_Retina_window_is_two_pixels_each_way_before_it_is_looked_for()
    {
        var scale = new Vector2(2, 2);

        // The menu as drawn, at 20 to 100 points: the UI's, not the bar's
        Assert.False(DesktopWindow.OnBar(Bar, Holes, new Vector2(60, 15) * scale));
        // The bar between the menu and the button, at 400 points: moves the window (taken as pixels it was in the menu)
        Assert.True(DesktopWindow.OnBar(Bar, Holes, new Vector2(400, 15) * scale));
        Assert.True(Holes[0].Contains(new PointF(100, 15)) && !DesktopWindow.OnBar(Bar, Holes, new Vector2(100, 15)));
        // 50 points down is under the 36 point bar: the UI's (taken as pixels it was on the bar)
        Assert.False(DesktopWindow.OnBar(Bar, Holes, new Vector2(400, 50) * scale));
        Assert.True(DesktopWindow.OnBar(Bar, Holes, new Vector2(400, 50)));
        // The close button at the right end, 760 points of an 800 point window
        Assert.False(DesktopWindow.OnBar(Bar, Holes, new Vector2(760, 15) * scale));
    }
}
