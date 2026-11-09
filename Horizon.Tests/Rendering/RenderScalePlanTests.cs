using Horizon.Graphics;
using Horizon.Rendering.Scaling;

namespace Horizon.Tests;

public class RenderScalePlanTests
{
    private const uint MAX = 16384;

    [Fact]
    public void The_default_is_native_and_draws_nothing_extra()
    {
        RenderScalePlan plan = RenderScalePlan.For(default(RenderScaleSettings) with { }, 1920, 1080, 1.0f, MAX);
        RenderScalePlan fresh = RenderScalePlan.For(new RenderScaleSettings(), 1920, 1080, 1.0f, MAX);

        Assert.False(fresh.Active);
        Assert.Equal((1920u, 1080u), (fresh.SceneWidth, fresh.SceneHeight));
        Assert.Equal(RenderScalePolicy.Native, fresh.Policy);
        Assert.Equal(1.0f, fresh.EffectiveScale);

        // A struct nobody set up at all (every field zero) is native too, it's sanitized rather than trusted
        Assert.False(plan.Active);
    }

    [Theory]
    [InlineData(0.75f, 1920u, 1080u, 1440u, 810u)]
    [InlineData(0.5f, 1920u, 1080u, 960u, 540u)]
    [InlineData(0.5f, 2560u, 1600u, 1280u, 800u)]
    [InlineData(0.7f, 2560u, 1600u, 1792u, 1120u)]
    [InlineData(0.5f, 1081u, 1u, 541u, 1u)]
    [InlineData(0.25f, 3u, 3u, 1u, 1u)]
    public void A_manual_scale_rounds_to_whole_pixels_halves_away_from_zero(float scale, uint width, uint height, uint sceneWidth, uint sceneHeight)
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.At(scale), width, height, 1.0f, MAX);

        Assert.Equal((sceneWidth, sceneHeight), (plan.SceneWidth, plan.SceneHeight));
        Assert.Equal((width, height), (plan.OutputWidth, plan.OutputHeight));
    }

    [Theory]
    [InlineData(float.NaN, 1.0f)]
    [InlineData(float.PositiveInfinity, 1.0f)]
    [InlineData(float.NegativeInfinity, 1.0f)]
    [InlineData(0.0f, 1.0f)]
    [InlineData(-0.5f, 1.0f)]
    [InlineData(1e9f, 1.0f)]
    [InlineData(1.5f, 1.0f)]
    [InlineData(0.0001f, RenderScaleSettings.SMALLEST)]
    [InlineData(float.Epsilon, RenderScaleSettings.SMALLEST)]
    public void Scales_that_make_no_sense_are_settled_the_same_way_every_time(float asked, float used)
    {
        RenderScaleSettings settings = RenderScaleSettings.At(asked).Sanitized();
        Assert.Equal(used, settings.Scale);

        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.At(asked), 1280, 720, 1.0f, MAX);
        Assert.True(plan.SceneWidth >= 1 && plan.SceneHeight >= 1);
        Assert.Equal(plan, RenderScalePlan.For(RenderScaleSettings.At(asked), 1280, 720, 1.0f, MAX));
    }

    [Fact]
    public void Sharpness_policy_and_kinds_nobody_defined_are_put_right()
    {
        RenderScaleSettings settings = new RenderScaleSettings
        {
            Policy = (RenderScalePolicy)42,
            Upscaler = (Upscaler)99,
            Composition = (RenderComposition)7,
            Sharpness = float.NaN
        }.Sanitized();

        Assert.Equal(RenderScalePolicy.Native, settings.Policy);
        Assert.Equal(Upscaler.Cas, settings.Upscaler);
        Assert.Equal(RenderComposition.NativeOverlay, settings.Composition);
        Assert.Equal(0.0f, settings.Sharpness);
        Assert.Equal(1.0f, (RenderScaleSettings.At(0.5f) with { Sharpness = 9.0f }).Sanitized().Sharpness);
    }

    [Fact]
    public void A_window_of_nothing_has_a_plan_of_nothing_that_does_nothing()
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.At(0.5f), 0, 0, 2.0f, MAX);

        Assert.False(plan.Active);
        Assert.Equal((0u, 0u), (plan.SceneWidth, plan.SceneHeight));
    }

    [Fact]
    public void The_scene_never_outgrows_the_gpu()
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.At(0.9f), 20000, 100, 1.0f, 4096);
        Assert.True(plan.SceneWidth <= 4096);

        RenderScalePlan none = RenderScalePlan.For(RenderScaleSettings.At(0.5f), 100, 100, 1.0f, 0);
        Assert.Equal((1u, 1u), (none.SceneWidth, none.SceneHeight));
    }

    [Theory]
    [InlineData(0.5f, false)]
    [InlineData(0.75f, false)]
    [InlineData(0.49f, true)]
    [InlineData(0.25f, true)]
    public void Cas_upscales_to_four_times_the_area_and_past_that_after_a_stretch(float scale, bool stretched)
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.At(scale), 1920, 1080, 1.0f, MAX);

        Assert.Equal(Upscaler.Cas, plan.Effective);
        Assert.Equal(stretched, plan.HasPrepass);
        Assert.Null(plan.Fallback);

        RenderScalePlan asked = RenderScalePlan.For(RenderScaleSettings.At(scale) with { BeyondLimit = UpscaleLimit.Bilinear }, 1920, 1080, 1.0f, MAX);
        Assert.Equal(stretched ? Upscaler.Bilinear : Upscaler.Cas, asked.Effective);
        Assert.Equal(stretched, asked.Fallback is not null);
    }

    [Theory]
    [InlineData(Upscaler.Bilinear)]
    [InlineData(Upscaler.Nearest)]
    public void Bilinear_and_nearest_are_used_as_asked_at_any_scale(Upscaler upscaler)
    {
        RenderScalePlan plan = RenderScalePlan.For(RenderScaleSettings.At(0.25f) with { Upscaler = upscaler }, 1920, 1080, 1.0f, MAX);

        Assert.Equal(upscaler, plan.Effective);
        Assert.Null(plan.Fallback);
    }

    [Fact]
    public void Cas_says_what_its_header_says_about_scaling()
    {
        // CasSupportScaling is (out area) / (in area) <= 4
        Assert.True(CasConstants.SupportsScaling(1920, 1080, 960, 540));
        Assert.True(CasConstants.SupportsScaling(1920, 1080, 1280, 720));
        Assert.False(CasConstants.SupportsScaling(1920, 1080, 959, 540));
        Assert.True(CasConstants.SupportsScaling(1920, 1080, 1920, 1080));
    }

    [Fact]
    public void A_custom_upscaler_is_used_when_it_can_and_cas_says_why_when_it_isnt()
    {
        var picky = new FakeUpscaler(most: 2.0f);

        RenderScalePlan fits = RenderScalePlan.For(RenderScaleSettings.At(0.75f) with { Upscaler = Upscaler.Custom, Custom = picky }, 1920, 1080, 1.0f, MAX);
        Assert.Equal(Upscaler.Custom, fits.Effective);
        Assert.Null(fits.Fallback);

        RenderScalePlan tooFar = RenderScalePlan.For(RenderScaleSettings.At(0.5f) with { Upscaler = Upscaler.Custom, Custom = picky }, 1920, 1080, 1.0f, MAX);
        Assert.Equal(Upscaler.Cas, tooFar.Effective);
        Assert.Contains("fake", tooFar.Fallback);

        RenderScalePlan none = RenderScalePlan.For(RenderScaleSettings.At(0.75f) with { Upscaler = Upscaler.Custom }, 1920, 1080, 1.0f, MAX);
        Assert.Equal(Upscaler.Cas, none.Effective);
        Assert.NotNull(none.Fallback);

        // Past what CAS reaches as well, CAS after a stretch, and why the custom one isn't used is said
        RenderScalePlan neither = RenderScalePlan.For(RenderScaleSettings.At(0.3f) with { Upscaler = Upscaler.Custom, Custom = picky }, 1920, 1080, 1.0f, MAX);
        Assert.Equal(Upscaler.Cas, neither.Effective);
        Assert.True(neither.HasPrepass);
        Assert.Contains("fake", neither.Fallback);
    }

    [Fact]
    public void Cas_setup_works_out_the_headers_constants()
    {
        CasConstants none = CasConstants.For(0.0f, 960, 540, 1920, 1080);
        Assert.Equal(0.5f, none.Scale.X);
        Assert.Equal(0.5f, none.Scale.Y);
        Assert.Equal(-0.25f, none.Offset.X);
        Assert.Equal(-1.0f / 8.0f, none.Peak);

        Assert.Equal(-1.0f / 5.0f, CasConstants.For(1.0f, 960, 540, 1920, 1080).Peak);
        Assert.Equal(-1.0f / 6.5f, CasConstants.For(0.5f, 960, 540, 1920, 1080).Peak, 6);
        Assert.Equal(-1.0f / 5.0f, CasConstants.For(7.0f, 960, 540, 1920, 1080).Peak);
        Assert.Equal(-1.0f / 8.0f, CasConstants.For(float.NaN, 960, 540, 1920, 1080).Peak);
    }

    private sealed class FakeUpscaler(float most) : ISpatialUpscaler
    {
        public string Name => "fake";

        public bool Supports(uint sceneWidth, uint sceneHeight, uint outputWidth, uint outputHeight) =>
            outputWidth * (double)outputHeight / (sceneWidth * (double)sceneHeight) <= most;

        public void Upscale(in SpatialUpscale pass) { }
    }
}
