using System.Numerics;

using Horizon.UI.Drawing;

namespace Horizon.Tests;

/// <summary>
/// Icons that move, without a window. A name split from its motion, the drawn icons found by name, and the motions' tweens
/// ticked by hand, a spin going round, a bounce up and landed, a beat swelling and resting.
/// </summary>
public sealed class UIAnimatedIconsTests
{
    [Fact]
    public void A_name_comes_apart_from_its_motion()
    {
        Assert.Equal("sync", UIAnimatedIcons.Split("sync~spin", out UIIconMotion spin).ToString());
        Assert.Equal(UIIconMotion.Spin, spin);
        Assert.Equal("bell", UIAnimatedIcons.Split("bell", out UIIconMotion none).ToString());
        Assert.Equal(UIIconMotion.None, none);
        Assert.Equal("bell", UIAnimatedIcons.Split("bell~wobble", out UIIconMotion unknown).ToString());
        Assert.Equal(UIIconMotion.None, unknown);
        Assert.All(UIAnimatedIcons.Motions, name => Assert.NotEqual(UIIconMotion.None, UIAnimatedIcons.MotionOf(name)));
    }

    [Fact]
    public void The_drawn_icons_are_found_by_name_and_with_a_motion_after_them()
    {
        Assert.True(UISymbols.TryGet("spinner", out char spinner, out _));
        Assert.True(UIAnimatedIcons.IsDrawn(spinner));
        Assert.True(UISymbols.TryGet("dots~pulse", out char dots, out UIIconMotion motion));
        Assert.True(UIAnimatedIcons.IsDrawn(dots));
        Assert.Equal(UIIconMotion.Pulse, motion);
        Assert.True(UISymbols.TryGet("pulse_dot", out _, out _));
        Assert.False(UISymbols.TryGet("not-an-icon-at-all", out _, out _));
    }

    // What a motion does over a round and a bit, sampled every few milliseconds as the tweens are ticked
    private static List<(float Turn, float Scale, Vector2 Offset, float Alpha)> Over(UIIconMotion motion, float seconds, float side = 20)
    {
        var seen = new List<(float, float, Vector2, float)>();
        for (float t = 0; t < seconds; t += 0.01f)
        {
            UIAnimatedIcons.Update(0.01f);
            seen.Add(UIAnimatedIcons.Now(motion, side));
        }

        return seen;
    }

    [Fact]
    public void A_spin_is_a_tween_that_goes_round_a_quarter_in_a_quarter_of_its_round()
    {
        UIAnimatedIcons.Update(0.0f);
        float before = UIAnimatedIcons.Now(UIIconMotion.Spin, 16).Turn;
        UIAnimatedIcons.Update(UIAnimatedIcons.SPIN_TIME * 0.25f);
        float turned = UIAnimatedIcons.Now(UIIconMotion.Spin, 16).Turn - before;
        float quarter = ((turned % MathF.Tau) + MathF.Tau) % MathF.Tau;
        Assert.Equal(MathF.Tau * 0.75f, quarter, 2);
    }

    [Fact]
    public void A_bounce_goes_up_and_lands_and_waits_on_the_ground()
    {
        var seen = Over(UIIconMotion.Bounce, UIAnimatedIcons.BOUNCE_TIME * 1.2f);
        Assert.True(seen.Max(each => each.Offset.Y) > 3.0f, "it never went up");
        Assert.True(seen.Count(each => each.Offset.Y == 0.0f) > 10, "it never rested on the ground");
    }

    [Fact]
    public void A_beat_swells_and_rests_and_a_breath_never_goes_out_altogether()
    {
        var beats = Over(UIIconMotion.Beat, UIAnimatedIcons.BEAT_TIME * 1.1f);
        Assert.True(beats.Max(each => each.Scale) > 1.12f);
        Assert.Contains(beats, each => each.Scale == 1.0f);
        Assert.All(Over(UIIconMotion.Breathe, 3.0f), each => Assert.InRange(each.Alpha, 0.39f, 1.0f));
        Assert.Equal((0.0f, 1.0f, Vector2.Zero, 1.0f), UIAnimatedIcons.Now(UIIconMotion.None, 16));
    }
}
