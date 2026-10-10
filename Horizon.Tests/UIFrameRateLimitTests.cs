using Horizon.UI;

namespace Horizon.Tests;

public class UIFrameRateLimitTests
{
    /// <summary>
    /// Runs frames at a steady rate for a while and counts the ones the UI would be drawn anew in, the way the
    /// compositor asks every frame. Also the fewest and the most frames there were from one of those to the next.
    /// </summary>
    private static (double PerSecond, int Fewest, int Most) Run(double fps, float limit, double seconds = 4.0, double wobble = 0.0)
    {
        double frame = 1.0 / fps, since = 0.0, time = 0.0;
        int refreshes = 0, gap = 0, fewest = int.MaxValue, most = 0, frames = 0;

        while (time < seconds)
        {
            // A frame is never quite as long as the one before it, least of all on a screen that says it is
            double took = frame * (1.0 + (frames % 2 == 0 ? wobble : -wobble));
            frames++;
            time += took;
            since += took;
            gap++;

            if (!UICompositor.RefreshDue(since, frame, limit))
                continue;

            refreshes++;
            fewest = Math.Min(fewest, gap);
            most = Math.Max(most, gap);
            since = 0.0;
            gap = 0;
        }

        return (refreshes / time, fewest, most);
    }

    [Fact]
    public void It_is_120_unless_somebody_says_otherwise() =>
        Assert.Equal(120.0f, UICompositor.DefaultFrameRateLimit);

    [Theory]
    [InlineData(1000.0)]
    [InlineData(3600.0)]
    [InlineData(9000.0)]
    public void A_game_running_flat_out_has_its_UI_drawn_about_120_times_a_second(double fps)
    {
        var (perSecond, fewest, most) = Run(fps, 120.0f);

        // Rounded to whole frames, so a little over when the frames are long, and always the same many frames apart
        Assert.InRange(perSecond, 119.0, 128.0);
        Assert.Equal(fewest, most);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(60.0)]
    [InlineData(120.0)]
    [InlineData(144.0)]     // nearer to every frame than to every other one, and five out of six is a judder
    [InlineData(165.0)]
    public void A_screen_at_or_near_the_limit_has_it_every_frame(double fps)
    {
        var (perSecond, fewest, most) = Run(fps, 120.0f, wobble: 0.04);

        Assert.Equal(1, fewest);
        Assert.Equal(1, most);
        Assert.Equal(fps, perSecond, 0);
    }

    [Theory]
    [InlineData(240.0, 2)]
    [InlineData(360.0, 3)]
    [InlineData(480.0, 4)]
    public void A_faster_screen_has_it_every_so_many_frames_and_never_unevenly(double fps, int every)
    {
        var (perSecond, fewest, most) = Run(fps, 120.0f, wobble: 0.04);

        Assert.Equal(every, fewest);
        Assert.Equal(every, most);
        Assert.Equal(120.0, perSecond, 0);
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(3600.0)]
    public void No_limit_is_every_frame(double fps)
    {
        var (_, fewest, most) = Run(fps, 0.0f);

        Assert.Equal(1, fewest);
        Assert.Equal(1, most);
    }

    [Fact]
    public void Another_limit_is_kept_to_as_well()
    {
        Assert.InRange(Run(3600.0, 30.0f).PerSecond, 29.5, 30.5);
        Assert.InRange(Run(3600.0, 240.0f).PerSecond, 238.0, 250.0);
    }
}
