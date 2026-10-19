using System.Numerics;

using Horizon.Engine;
using Horizon.UI.Drawing;

namespace Horizon.Tests;

// The glass a component sits on (UIGlass), the sums of it that need no GPU. Where a pane lands in pixels, the
// background that goes see through over it, and a pane between two ticks.
public class UIGlassTests
{
    [Fact]
    public void A_pane_lands_where_the_camera_shows_it_counted_from_the_top_left()
    {
        // A camera of 800 by 600 looking at the middle of the world, so the world's 0,0 is the middle of the picture
        var camera = new Camera2D(new Vector2(800, 600));
        Vector2 size = new(800, 600);

        // 100 wide and 50 high, its bottom left corner at the middle of the picture, Y going up
        var pane = new UIGlassPane(0, new Vector2(0, 0), new Vector2(100, 50),
            new Vector2(float.NegativeInfinity), new Vector2(float.PositiveInfinity), 8.0f, 18.0f, 1.0f);

        var (rect, clip) = UIGlassPass.Pixels(pane, camera, size);

        var seen = camera.Bounds;
        Vector2 offset = new(seen.X + seen.Width * 0.5f, seen.Y + seen.Height * 0.5f);
        Assert.Equal(400.0f - offset.X, rect.X, 0.5f);
        Assert.Equal(500.0f - offset.X, rect.Z, 0.5f);

        // The top of it is higher up the picture, which is fewer pixels down
        Assert.Equal(250.0f + offset.Y, rect.Y, 0.5f);
        Assert.Equal(300.0f + offset.Y, rect.W, 0.5f);

        // Nothing cut off it, the clip is as far as there is
        Assert.True(float.IsNegativeInfinity(clip.X) && float.IsPositiveInfinity(clip.Z));
        Assert.Equal(1.0f, UIGlassPass.PixelsPerUnit(camera, size), 0.001f);
    }

    [Fact]
    public void The_background_over_it_keeps_what_the_glass_does_not_take()
    {
        var glass = new UIGlass(Blur: 20.0f, Clarity: 0.25f);
        Vector4 over = glass.Over(new Vector4(0.2f, 0.3f, 0.4f, 1.0f));

        Assert.Equal(new Vector3(0.2f, 0.3f, 0.4f), new Vector3(over.X, over.Y, over.Z));
        Assert.Equal(0.75f, over.W, 0.001f);
        Assert.Equal(0.0f, new UIGlass(Clarity: 1.0f).Over(Vector4.One).W, 0.001f);
    }

    [Fact]
    public void Between_two_ticks_a_pane_slides_and_fades_with_its_component()
    {
        Vector2 none = new(float.NegativeInfinity), all = new(float.PositiveInfinity);
        var a = new UIGlassPane(4, new Vector2(0, 0), new Vector2(100, 100), none, all, 6.0f, 18.0f, 0.0f);
        var b = new UIGlassPane(9, new Vector2(100, 0), new Vector2(200, 100), none, all, 6.0f, 18.0f, 1.0f);

        UIGlassPane half = UIGlassPane.Blend(a, b, 0.5f);

        Assert.Equal(new Vector2(50, 0), half.Min);
        Assert.Equal(0.5f, half.Opacity, 0.001f);
        Assert.Equal(9, half.Start);

        // An unclipped pane stays unclipped, infinity blended with itself would be no number at all
        Assert.True(float.IsNegativeInfinity(half.ClipMin.X) && float.IsPositiveInfinity(half.ClipMax.Y));
    }
}
