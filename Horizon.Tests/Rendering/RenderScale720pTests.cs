using Horizon.Platform;
using Horizon.Rendering.Scaling;

namespace Horizon.Tests;

public class RenderScale720pTests
{
    private const uint MAX = 16384;

    [Theory]
    // The tablet, either way up
    [InlineData(2560u, 1600u, 1152u, 720u)]
    [InlineData(1600u, 2560u, 720u, 1152u)]
    // A tall phone keeps its shape
    [InlineData(1080u, 2400u, 720u, 1600u)]
    [InlineData(2400u, 1080u, 1600u, 720u)]
    // Every 16 by 9 is exactly 1280 by 720
    [InlineData(1920u, 1080u, 1280u, 720u)]
    [InlineData(2560u, 1440u, 1280u, 720u)]
    [InlineData(3840u, 2160u, 1280u, 720u)]
    [InlineData(1080u, 1920u, 720u, 1280u)]
    // An odd one rounds its long edge to the nearest pixel, 1000 * 720 / 999 is 720.72
    [InlineData(1000u, 999u, 721u, 720u)]
    public void Auto_draws_the_short_edge_at_720_and_keeps_the_windows_shape(uint width, uint height, uint sceneWidth, uint sceneHeight)
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.Auto, width, height, 2.0f, MAX);

        Assert.Equal((sceneWidth, sceneHeight), (plan.SceneWidth, plan.SceneHeight));
        Assert.Equal((width, height), (plan.OutputWidth, plan.OutputHeight));
        Assert.Equal(RenderScalePolicy.Automatic, plan.Policy);
        Assert.True(plan.Active);
    }

    [Theory]
    [InlineData(1280u, 720u)]
    [InlineData(720u, 1600u)]
    [InlineData(800u, 600u)]
    [InlineData(640u, 480u)]
    [InlineData(1u, 5000u)]
    [InlineData(5000u, 1u)]
    public void A_window_with_a_short_edge_of_720_or_less_is_never_drawn_bigger(uint width, uint height)
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.Auto, width, height, 3.0f, MAX);

        Assert.False(plan.Active);
        Assert.Equal((width, height), (plan.SceneWidth, plan.SceneHeight));
    }

    [Fact]
    public void A_window_of_nothing_is_nothing_and_never_divides_by_zero()
    {
        RenderScalePlan none = RenderScalePlan.For(RenderScaleSettings.Auto, 0, 0, 2.0f, MAX);
        Assert.False(none.Active);
        Assert.Equal(1.0f, none.RequestedScale);

        RenderScalePlan half = RenderScalePlan.For(RenderScaleSettings.Auto, 0, 2400, 2.0f, MAX);
        Assert.False(half.Active);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(2.0f)]
    [InlineData(3.5f)]
    [InlineData(float.NaN)]
    public void The_density_doesnt_change_720p(float density)
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.Auto, 2560, 1600, density, MAX);
        Assert.Equal((1152u, 720u), (plan.SceneWidth, plan.SceneHeight));
    }

    [Fact]
    public void The_tablet_is_720p_and_never_the_old_heuristics_1280_by_800()
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.Auto, 2560, 1600, 2.0f, MAX);

        Assert.NotEqual((1280u, 800u), (plan.SceneWidth, plan.SceneHeight));
        Assert.Equal(0.45f, plan.RequestedScale, 4);
        Assert.Equal(0.45f, plan.EffectiveScale, 4);
    }

    [Fact]
    public void Cas_past_its_reach_is_still_cas_after_a_stretch_to_half_the_window()
    {
        // 2560 by 1600 over 1152 by 720 is 4.94 times the area, CAS's header stops at 4
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.Auto, 2560, 1600, 2.0f, MAX);
        Assert.False(CasConstants.SupportsScaling(2560, 1600, 1152, 720));

        Assert.Equal(Upscaler.Cas, plan.Effective);
        Assert.True(plan.HasPrepass);
        Assert.Equal((1280u, 800u), (plan.PrepassWidth, plan.PrepassHeight));
        Assert.Null(plan.Fallback);

        // What CAS is given is never more than 4 times the area from the window
        Assert.True(2560.0 * 1600.0 / (plan.PrepassWidth * (double)plan.PrepassHeight) <= CasConstants.AREA_LIMIT);
    }

    [Theory]
    [InlineData(1920u, 1080u)]
    [InlineData(1080u, 2400u)]
    public void Within_cas_reach_there_is_no_stretch(uint width, uint height)
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.Auto, width, height, 2.0f, MAX);

        Assert.Equal(Upscaler.Cas, plan.Effective);
        Assert.False(plan.HasPrepass);
    }

    [Theory]
    [InlineData(2561u, 1601u)]
    [InlineData(4001u, 2999u)]
    [InlineData(7u, 5u)]
    public void The_stretch_is_always_within_cas_reach_and_never_smaller_than_the_scene(uint width, uint height)
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.At(0.25f), width, height, 1.0f, MAX);
        if (!plan.HasPrepass)
            return;

        Assert.True(width * (double)height / (plan.PrepassWidth * (double)plan.PrepassHeight) <= CasConstants.AREA_LIMIT);
        Assert.True(plan.PrepassWidth >= plan.SceneWidth && plan.PrepassHeight >= plan.SceneHeight);
        Assert.True(plan.PrepassWidth <= width && plan.PrepassHeight <= height);
    }

    [Fact]
    public void Bilinear_past_cas_reach_is_there_when_asked_for_and_says_so()
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.Auto with { BeyondLimit = UpscaleLimit.Bilinear }, 2560, 1600, 2.0f, MAX);

        Assert.Equal(Upscaler.Bilinear, plan.Effective);
        Assert.False(plan.HasPrepass);
        Assert.Contains("bilinear", plan.Fallback);
    }

    [Fact]
    public void Another_short_edge_can_be_asked_for()
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.ShortEdgeOf(1080), 2560, 1600, 2.0f, MAX);
        Assert.Equal((1728u, 1080u), (plan.SceneWidth, plan.SceneHeight));

        RenderScalePlan nothing = RenderScalePlan.For(RenderScaleSettings.ShortEdgeOf(0), 2560, 1600, 2.0f, MAX);
        Assert.Equal((1152u, 720u), (nothing.SceneWidth, nothing.SceneHeight));
    }

    [Fact]
    public void Manual_and_native_still_win_over_720p()
    {
        Assert.False(RenderScalePlan.For(RenderScaleSettings.Native, 2560, 1600, 2.0f, MAX).Active);
        Assert.Equal(1920u, RenderScalePlan.For(RenderScaleSettings.At(0.75f), 2560, 1600, 2.0f, MAX).SceneWidth);
    }

    [Theory]
    [InlineData("720p", 720u)]
    [InlineData("1080p", 1080u)]
    [InlineData("auto", 720u)]
    public void The_setting_reads_a_short_edge(string text, uint edge)
    {
        Assert.True(RenderScaleEnvironment.TryScale(text, out RenderScaleSettings settings));
        Assert.Equal(RenderScalePolicy.Automatic, settings.Policy);
        Assert.Equal(edge, settings.Sanitized().ShortEdge);
        Assert.Contains($"{edge}p", RenderScaleEnvironment.Format(settings));
    }

    [Fact]
    public void Density_is_the_old_heuristic_on_request()
    {
        Assert.True(RenderScaleEnvironment.TryScale("density", out RenderScaleSettings settings));
        Assert.Equal(RenderScalePolicy.Density, settings.Policy);
        Assert.Equal((1792u, 1120u), (RenderScalePlan.For(settings, 2560, 1600, 2.0f, MAX).SceneWidth, RenderScalePlan.For(settings, 2560, 1600, 2.0f, MAX).SceneHeight));
    }

    [Fact]
    public void Phones_and_tablets_start_at_720p_and_desktops_native()
    {
        if (RenderScaleEnvironment.InEnvironment)
            return;

        RenderScaleSettings phone = RenderScaleEnvironment.Startup(null, PlatformFeatures.AutomaticRenderScale, "test");
        Assert.Equal(RenderScalePolicy.Automatic, phone.Policy);
        Assert.Equal(720u, phone.Sanitized().ShortEdge);
        Assert.Equal(RenderScalePolicy.Native, RenderScaleEnvironment.Startup(null, PlatformFeatures.Window, "test").Policy);
    }
}
