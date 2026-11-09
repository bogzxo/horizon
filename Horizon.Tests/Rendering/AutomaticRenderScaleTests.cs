using Horizon.Platform;
using Horizon.Rendering.Scaling;

namespace Horizon.Tests;

public class AutomaticRenderScaleTests
{
    private static readonly DensityRenderScale Policy = DensityRenderScale.Default;
    private static readonly RenderScaleSettings ByDensity = new() { Policy = RenderScalePolicy.Density };

    [Theory]
    // A 1080p laptop at 100% and a small window aren't touched
    [InlineData(1920u, 1080u, 1.0f, 1.0f)]
    [InlineData(1366u, 768u, 1.0f, 1.0f)]
    // A Chromebook's freeform window at 125%
    [InlineData(1200u, 800u, 1.25f, 1.0f)]
    // A 10.5 inch tablet at 2560 by 1600 and xhdpi is over the 1080p budget twice, sqrt(1/1.975) is 0.71
    [InlineData(2560u, 1600u, 2.0f, 0.7f)]
    [InlineData(1600u, 2560u, 2.0f, 0.7f)]
    // A 1080 by 2400 phone at density 3 is denser than xhdpi, 2/3 rounded down to the step
    [InlineData(1080u, 2400u, 3.0f, 0.65f)]
    // A 1440 by 3200 phone at 3.5 goes to the minimum's neighbourhood by the density and the budget both
    [InlineData(1440u, 3200u, 3.5f, 0.55f)]
    // A 720p phone at 2 has nothing to gain
    [InlineData(720u, 1600u, 2.0f, 1.0f)]
    public void The_scale_follows_the_surfaces_pixels_and_the_density(uint width, uint height, float density, float expected)
    {
        Assert.Equal(expected, Policy.ScaleFor(width, height, density), 3);
    }

    [Fact]
    public void A_density_change_alone_changes_the_density_plan()
    {
        RenderScalePlan before = RenderScalePlan.For(ByDensity, 1080, 2400, 2.0f, 16384);
        RenderScalePlan after = RenderScalePlan.For(ByDensity, 1080, 2400, 3.0f, 16384);

        Assert.NotEqual(before, after);
        Assert.NotEqual(before.SceneWidth, after.SceneWidth);
        Assert.Equal(before.OutputWidth, after.OutputWidth);
    }

    [Fact]
    public void Turning_the_device_doesnt_change_the_scale()
    {
        float portrait = Policy.ScaleFor(1600, 2560, 2.0f), landscape = Policy.ScaleFor(2560, 1600, 2.0f);
        Assert.Equal(portrait, landscape);

        RenderScalePlan turned = RenderScalePlan.For(ByDensity, 1600, 2560, 2.0f, 16384);
        Assert.Equal((1120u, 1792u), (turned.SceneWidth, turned.SceneHeight));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(0.0f)]
    [InlineData(-2.0f)]
    [InlineData(float.PositiveInfinity)]
    public void A_density_that_isnt_one_is_taken_as_1(float density)
    {
        Assert.Equal(Policy.ScaleFor(1920, 1080, 1.0f), Policy.ScaleFor(1920, 1080, density));
    }

    [Fact]
    public void Nothing_brings_it_under_the_minimum_or_over_1()
    {
        Assert.Equal(Policy.Minimum, Policy.ScaleFor(7680, 4320, 8.0f));
        Assert.Equal(1.0f, Policy.ScaleFor(1, 1, 0.5f));

        var odd = new DensityRenderScale { Minimum = float.NaN, Step = -1.0f, PixelBudget = 0.0, TargetDensity = float.NaN, NativeFrom = 5.0f };
        float scale = odd.ScaleFor(2560, 1600, 2.0f);
        Assert.InRange(scale, RenderScaleSettings.SMALLEST, 1.0f);
    }

    [Fact]
    public void A_manual_scale_wins_over_the_automatic_one_and_native_opts_out()
    {
        RenderScalePlan manual = RenderScalePlan.For(RenderScaleSettings.At(0.9f), 2560, 1600, 2.0f, 16384);
        Assert.Equal(2304u, manual.SceneWidth);

        RenderScalePlan native = RenderScalePlan.For(RenderScaleSettings.Native, 2560, 1600, 3.5f, 16384);
        Assert.False(native.Active);
    }

    [Fact]
    public void A_phone_starts_automatic_and_a_desktop_native_unless_told()
    {
        // Only meaningful with none of the settings in this process's environment, which a test run hasn't
        if (Environment.GetEnvironmentVariable(RenderScaleEnvironment.SCALE) is not null)
            return;

        Assert.Equal(RenderScalePolicy.Automatic, RenderScaleEnvironment.Startup(null, PlatformFeatures.AutomaticRenderScale, "test").Policy);
        Assert.Equal(RenderScalePolicy.Native, RenderScaleEnvironment.Startup(null, PlatformFeatures.Window, "test").Policy);

        // What the game configured wins over the platform's idea
        Assert.Equal(RenderScalePolicy.Native, RenderScaleEnvironment.Startup(RenderScaleSettings.Native, PlatformFeatures.AutomaticRenderScale, "test").Policy);
    }

    [Theory]
    [InlineData("0.75", RenderScalePolicy.Manual, 0.75f)]
    [InlineData("75%", RenderScalePolicy.Manual, 0.75f)]
    [InlineData(" 0.5 ", RenderScalePolicy.Manual, 0.5f)]
    [InlineData("native", RenderScalePolicy.Native, 1.0f)]
    [InlineData("off", RenderScalePolicy.Native, 1.0f)]
    [InlineData("1", RenderScalePolicy.Native, 1.0f)]
    [InlineData("100%", RenderScalePolicy.Native, 1.0f)]
    [InlineData("auto", RenderScalePolicy.Automatic, 1.0f)]
    public void The_scale_setting_reads_numbers_percentages_and_words(string text, RenderScalePolicy policy, float scale)
    {
        var problems = new List<string>();
        RenderScaleSettings settings = RenderScaleEnvironment.Apply(RenderScaleSettings.Auto, Read(RenderScaleEnvironment.SCALE, text), problems);

        Assert.Empty(problems);
        Assert.Equal(policy, settings.Policy);
        if (policy == RenderScalePolicy.Manual)
            Assert.Equal(scale, settings.Scale, 5);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("75")]
    [InlineData("150%")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("half")]
    public void A_scale_setting_that_cant_be_read_is_left_out_and_said(string text)
    {
        var problems = new List<string>();
        RenderScaleSettings settings = RenderScaleEnvironment.Apply(RenderScaleSettings.Auto, Read(RenderScaleEnvironment.SCALE, text), problems);

        Assert.Single(problems);
        Assert.Equal(RenderScalePolicy.Automatic, settings.Policy);
    }

    [Fact]
    public void The_upscaler_sharpness_and_composition_settings_are_read()
    {
        var values = new Dictionary<string, string>
        {
            [RenderScaleEnvironment.SCALE] = "0.6",
            [RenderScaleEnvironment.UPSCALER] = "Nearest",
            [RenderScaleEnvironment.SHARPNESS] = "0.4",
            [RenderScaleEnvironment.COMPOSITION] = "whole"
        };
        var problems = new List<string>();
        RenderScaleSettings settings = RenderScaleEnvironment.Apply(RenderScaleSettings.Native, name => values.GetValueOrDefault(name), problems);

        Assert.Empty(problems);
        Assert.Equal((RenderScalePolicy.Manual, 0.6f, Upscaler.Nearest, 0.4f, RenderComposition.WholeFrame),
            (settings.Policy, settings.Scale, settings.Upscaler, settings.Sharpness, settings.Composition));

        values[RenderScaleEnvironment.UPSCALER] = "dlss";
        values[RenderScaleEnvironment.SHARPNESS] = "lots";
        values[RenderScaleEnvironment.COMPOSITION] = "sideways";
        problems.Clear();
        RenderScaleSettings unread = RenderScaleEnvironment.Apply(RenderScaleSettings.Native, name => values.GetValueOrDefault(name), problems);
        Assert.Equal(3, problems.Count);
        Assert.Equal(Upscaler.Cas, unread.Upscaler);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("native")]
    [InlineData("0.75")]
    [InlineData("0.33")]
    public void What_is_kept_in_a_file_comes_back_the_same(string scale)
    {
        Assert.True(RenderScaleEnvironment.TryScale(scale, out RenderScaleSettings settings));
        settings = settings with { Upscaler = Upscaler.Nearest, Sharpness = 0.25f, Composition = RenderComposition.WholeFrame };

        var problems = new List<string>();
        RenderScaleSettings back = RenderScaleEnvironment.Parse(RenderScaleSettings.Auto, RenderScaleEnvironment.Format(settings), problems);

        Assert.Empty(problems);
        Assert.Equal(settings.Sanitized(), back.Sanitized());
    }

    private static Func<string, string?> Read(string name, string value) => asked => asked == name ? value : null;
}
