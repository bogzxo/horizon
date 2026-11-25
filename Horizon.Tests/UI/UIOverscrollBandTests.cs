using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>The rubber band at the ends for a Mac's trackpad: held by the fingers, sprung back when they lift, bounced by the glide.</summary>
public class UIOverscrollBandTests : IDisposable
{
    public UIOverscrollBandTests() => (UIWheel.Held, UIWheel.Gliding, UIWheel.Seconds) = (false, false, 1.0f / 120.0f);

    public void Dispose() => (UIWheel.Held, UIWheel.Gliding, UIWheel.Seconds) = (false, false, 0.0f);

    [Fact]
    public void The_band_gives_less_the_further_it_is_pulled_and_undoes()
    {
        Assert.Equal(0.0f, UIOverscroll.Band(0.0f, 600.0f));
        float near = UIOverscroll.Band(50.0f, 600.0f), far = UIOverscroll.Band(500.0f, 600.0f) - UIOverscroll.Band(450.0f, 600.0f);
        Assert.InRange(near, 20.0f, 50.0f);
        Assert.True(far < near);
        Assert.True(UIOverscroll.Band(100000.0f, 600.0f) < 600.0f);
        Assert.Equal(-UIOverscroll.Band(80.0f, 600.0f), UIOverscroll.Band(-80.0f, 600.0f), 4);
        Assert.Equal(80.0f, UIOverscroll.Unband(UIOverscroll.Band(80.0f, 600.0f), 600.0f), 2);
    }

    [Fact]
    public void Fingers_hold_it_out_past_the_end_and_it_springs_back_when_they_lift()
    {
        var over = new UIOverscroll();
        float position = 10.0f;
        UIWheel.Held = true;

        // Up by 40 from 10: 10 to the top, 30 into the band
        Assert.True(over.Scroll(ref position, -40.0f, 1000.0f, 600.0f, 1.0f));
        Assert.Equal(0.0f, position);
        Assert.True(over.Held);
        Assert.InRange(over.Stretch, -30.0f, -10.0f);

        // Held still for a while, it stays out
        float held = over.Stretch;
        for (int i = 0; i < 60; i++) over.Step(1.0f / 120.0f, 1.0f);
        Assert.Equal(held, over.Stretch);

        // Lifted, it goes back to the end
        UIWheel.Held = false;
        for (int i = 0; i < 240; i++) over.Step(1.0f / 120.0f, 1.0f);
        Assert.False(over.Active);
        Assert.False(over.Held);
    }

    [Fact]
    public void Moving_back_in_takes_the_stretch_first_then_scrolls()
    {
        var over = new UIOverscroll();
        float position = 0.0f;
        UIWheel.Held = true;
        over.Scroll(ref position, -30.0f, 1000.0f, 600.0f, 1.0f);
        Assert.Equal(0.0f, position);

        // Back down by 50: 30 of it undoes the pull, 20 scrolls
        over.Scroll(ref position, 50.0f, 1000.0f, 600.0f, 1.0f);
        Assert.Equal(20.0f, position, 3);
        Assert.False(over.Active);
    }

    [Fact]
    public void A_glide_bounces_off_the_end_once()
    {
        var over = new UIOverscroll();
        float position = 995.0f;
        UIWheel.Gliding = true;
        Assert.True(over.Scroll(ref position, 20.0f, 1000.0f, 600.0f, 1.0f));
        Assert.Equal(1000.0f, position);
        Assert.True(over.Velocity > 0.0f);

        float velocity = over.Velocity;
        over.Scroll(ref position, 15.0f, 1000.0f, 600.0f, 1.0f);
        Assert.Equal(velocity, over.Velocity);

        UIWheel.Gliding = false;
        float furthest = 0.0f;
        for (int i = 0; i < 240; i++)
        {
            over.Step(1.0f / 120.0f, 1.0f);
            furthest = MathF.Max(furthest, over.Stretch);
        }

        Assert.True(furthest > 5.0f);
        Assert.False(over.Active);
    }

    [Fact]
    public void A_log_scrolled_to_its_end_before_it_was_laid_out_scrolls_up_without_stretching()
    {
        // ScrollPanel.ScrollToEnd leaves the offset at float.MaxValue until the next layout
        var over = new UIOverscroll();
        float position = float.MaxValue;
        UIWheel.Held = true;
        Assert.True(over.Scroll(ref position, -30.0f, 1000.0f, 600.0f, 1.0f));
        Assert.Equal(970.0f, position);
        Assert.False(over.Active);

        var ease = new UIScrollEase();
        ease.Wheel(float.MaxValue, -48.0f, 1000.0f);
        Assert.Equal(952.0f, ease.Goal);
        Assert.False(ease.Over.Active);
    }

    [Fact]
    public void A_bounce_off_the_bottom_overshooting_past_the_top_isnt_caught_by_fingers_scrolling_up()
    {
        // The spring swung a hair past the top while the list was at its bottom, then the fingers landed and went up
        var over = new UIOverscroll { Stretch = -0.06f, Velocity = 1.1f };
        float position = 1000.0f;
        UIWheel.Held = true;
        Assert.True(over.Scroll(ref position, -12.0f, 1000.0f, 600.0f, 1.0f));
        Assert.Equal(988.0f, position);
        Assert.False(over.Active);
    }

    [Fact]
    public void A_turn_with_no_phase_at_an_end_is_for_what_it_is_in()
    {
        var over = new UIOverscroll();
        float position = 0.0f;
        Assert.False(over.Scroll(ref position, -10.0f, 1000.0f, 600.0f, 1.0f));
        Assert.False(over.Active);
        Assert.True(over.Scroll(ref position, 10.0f, 1000.0f, 600.0f, 1.0f));
        Assert.Equal(10.0f, position);
    }
}
