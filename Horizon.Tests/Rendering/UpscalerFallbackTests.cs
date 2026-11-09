using Horizon.Rendering.Scaling;

namespace Horizon.Tests;

public class UpscalerFallbackTests
{
    [Theory]
    [InlineData(Upscaler.Fsr1)]
    [InlineData(Upscaler.Gsr1)]
    [InlineData(Upscaler.Nearest)]
    public void Missing_variant_uses_cas_and_preserves_requested_setting(Upscaler missing)
    {
        var settings = RenderScaleSettings.Auto with { Upscaler = missing };
        var plan = RenderScalePlan.For(settings, 2560, 1600, 2, 16384);
        var actual = FrameScaler.ResolveAvailablePlan(settings, plan, 16384, u => u != missing);
        Assert.Equal(missing, actual.Requested);
        Assert.Equal(missing, settings.Upscaler);
        Assert.Equal(Upscaler.Cas, actual.Effective);
        Assert.Equal(720u, actual.SceneHeight);
        Assert.True(actual.HasPrepass);
        Assert.NotNull(actual.Fallback);
    }

    [Fact]
    public void Missing_cas_uses_bilinear_without_an_unneeded_prepass()
    {
        var settings = RenderScaleSettings.Auto with { Upscaler = Upscaler.Fsr1 };
        var actual = FrameScaler.ResolveAvailablePlan(settings, RenderScalePlan.For(settings, 2560, 1600, 2, 16384), 16384, u => u == Upscaler.Bilinear);
        Assert.Equal(Upscaler.Bilinear, actual.Effective);
        Assert.False(actual.HasPrepass);
        Assert.Equal(Upscaler.Fsr1, actual.Requested);
    }

    [Fact]
    public void No_working_pass_disables_scene_redirection_instead_of_showing_black()
    {
        var settings = RenderScaleSettings.At(0.5f) with { Upscaler = Upscaler.Fsr1 };
        var actual = FrameScaler.ResolveAvailablePlan(settings, RenderScalePlan.For(settings, 1600, 900, 1, 16384), 16384, _ => false);
        Assert.False(actual.Active);
        Assert.False(actual.HasPrepass);
        Assert.Equal(Upscaler.Fsr1, actual.Requested);
        Assert.Contains("native", actual.Fallback);
    }

    [Fact]
    public void Native_does_not_probe_or_load_any_shader()
    {
        var settings = RenderScaleSettings.Native;
        var plan = RenderScalePlan.For(settings, 1600, 900, 1, 16384);
        Assert.Same(plan, FrameScaler.ResolveAvailablePlan(settings, plan, 16384, _ => throw new Exception("Native probed a shader")));
    }

    [Fact]
    public void Missing_prepass_also_recovers_before_scene_redirection()
    {
        var settings = RenderScaleSettings.Auto;
        var actual = FrameScaler.ResolveAvailablePlan(settings, RenderScalePlan.For(settings, 2560, 1600, 2, 16384), 16384, u => u != Upscaler.Bilinear);
        Assert.False(actual.Active);
    }
}
