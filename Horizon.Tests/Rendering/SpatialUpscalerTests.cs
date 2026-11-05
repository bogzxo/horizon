using System.Numerics;

using Horizon.Rendering.Scaling;

namespace Horizon.Tests;

public class SpatialUpscalerTests
{
    [Theory]
    [InlineData("gsr1", Upscaler.Gsr1)]
    [InlineData("SGSR", Upscaler.Gsr1)]
    [InlineData("fsr1", Upscaler.Fsr1)]
    [InlineData("easu", Upscaler.Fsr1)]
    [InlineData("cas", Upscaler.Cas)]
    public void The_upscaler_setting_reads_gsr_and_fsr(string text, Upscaler upscaler)
    {
        var problems = new List<string>();
        RenderScaleSettings settings = RenderScaleEnvironment.Apply(RenderScaleSettings.Auto, name => name == RenderScaleEnvironment.UPSCALER ? text : null, problems);

        Assert.Empty(problems);
        Assert.Equal(upscaler, settings.Upscaler);
        Assert.Equal(upscaler, RenderScaleEnvironment.Parse(RenderScaleSettings.Native, RenderScaleEnvironment.Format(settings), problems).Upscaler);
    }

    [Fact]
    public void Sharpness_goes_to_each_upscaler_in_its_own_units()
    {
        // RCAS counts stops below its most, GSR 1's edge sharpness is 1 to 2 by its README
        Assert.Equal(2.0f, SpatialSharpness.RcasStops(0.0f));
        Assert.Equal(0.0f, SpatialSharpness.RcasStops(1.0f));
        Assert.Equal(0.25f, SpatialSharpness.RcasAmount(0.0f), 6);
        Assert.Equal(1.0f, SpatialSharpness.RcasAmount(1.0f), 6);
        Assert.Equal(1.0f, SpatialSharpness.GsrEdgeSharpness(0.0f));
        Assert.Equal(2.0f, SpatialSharpness.GsrEdgeSharpness(1.0f));
        Assert.Equal(1.0f, SpatialSharpness.GsrEdgeSharpness(float.NaN));
        Assert.Equal(2.0f, SpatialSharpness.GsrEdgeSharpness(9.0f));
    }

    [Fact]
    public void Easu_constants_are_the_headers()
    {
        EasuConstants c = EasuConstants.For(1280, 720, 2560, 1440);
        Assert.Equal(new Vector4(0.5f, 0.5f, -0.25f, -0.25f), c.Con0);
        Assert.Equal(new Vector4(1.0f / 1280, 1.0f / 720, 1.0f / 1280, -1.0f / 720), c.Con1);
        Assert.Equal(new Vector4(-1.0f / 1280, 2.0f / 720, 1.0f / 1280, 2.0f / 720), c.Con2);
        Assert.Equal(new Vector4(0.0f, 4.0f / 720, 0.0f, 0.0f), c.Con3);
        Assert.Equal(new Vector4(1.0f / 1280, 1.0f / 720, 1280, 720), GsrConstants.ViewportInfo(1280, 720));
    }

    [Theory]
    [InlineData(Upscaler.Cas)]
    [InlineData(Upscaler.Fsr1)]
    public void Cas_and_fsr_go_to_four_times_the_area_and_past_that_after_a_stretch(Upscaler upscaler)
    {
        RenderScalePlan within = RenderScalePlan.For(RenderScaleSettings.At(0.5f) with { Upscaler = upscaler }, 1920, 1080, 1.0f, 16384);
        Assert.Equal(upscaler, within.Effective);
        Assert.False(within.HasPrepass);

        RenderScalePlan past = RenderScalePlan.For(RenderScaleSettings.Auto with { Upscaler = upscaler }, 2560, 1600, 2.0f, 16384);
        Assert.Equal(upscaler, past.Effective);
        Assert.Equal((1280u, 800u), (past.PrepassWidth, past.PrepassHeight));

        RenderScalePlan asked = RenderScalePlan.For(RenderScaleSettings.Auto with { Upscaler = upscaler, BeyondLimit = UpscaleLimit.Bilinear }, 2560, 1600, 2.0f, 16384);
        Assert.Equal(Upscaler.Bilinear, asked.Effective);
        Assert.Contains(RenderScalePlan.NameOf(upscaler), asked.Fallback);
    }

    [Fact]
    public void Gsr_scales_the_720p_tablet_directly_without_inheriting_cas_limit()
    {
        foreach (var limit in new[] { UpscaleLimit.Prepass, UpscaleLimit.Bilinear })
        {
            var plan = RenderScalePlan.For(RenderScaleSettings.Auto with { Upscaler = Upscaler.Gsr1, BeyondLimit = limit }, 2560, 1600, 2, 16384);
            Assert.Equal(Upscaler.Gsr1, plan.Effective);
            Assert.Equal((1152u, 720u), (plan.SceneWidth, plan.SceneHeight));
            Assert.False(plan.HasPrepass);
            Assert.Null(plan.Fallback);
        }
    }

    [Fact]
    public void Gsr_high_contrast_correction_stays_within_upstream_23_byte_limit()
    {
        const int width = 18, height = 9, outputWidth = 40, outputHeight = 20;
        var source = new Vector4[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                source[y * width + x] = new Vector4(x % 3 == 0 ? 1 : 0, (x + y) % 2, y % 3 == 0 ? 1 : 0, (x % 5) / 4.0f);
        foreach (float sharpness in new[] { 0.0f, 1.0f })
        {
            var output = SpatialReference.Gsr1(source, width, height, outputWidth, outputHeight, sharpness);
            for (int y = 0; y < outputHeight; y++)
                for (int x = 0; x < outputWidth; x++)
                {
                    Vector4 sample = SpatialReference.Sample(source, width, height, (x + 0.5f) / outputWidth, (y + 0.5f) / outputHeight);
                    Vector4 pixel = output[y * outputWidth + x];
                    Vector4 delta = Vector4.Abs(pixel - sample);
                    Assert.True(float.IsFinite(pixel.X) && float.IsFinite(pixel.Y) && float.IsFinite(pixel.Z));
                    Assert.True(Math.Max(delta.X, Math.Max(delta.Y, delta.Z)) <= 23.0f / 255.0f + 0.00001f);
                    Assert.Equal(sample.W, pixel.W);
                }
        }
    }

    [Fact]
    public void Flat_colour_stays_flat_through_fsr_and_gsr()
    {
        var colour = new Vector4(0.3f, 0.6f, 0.2f, 1.0f);
        var source = new Vector4[12 * 9];
        Array.Fill(source, colour);

        foreach (Vector4 p in SpatialReference.Fsr1(source, 12, 9, 20, 15, 1.0f))
            Assert.True(Vector4.Distance(p, colour) < 1.0f / 255.0f);
        foreach (Vector4 p in SpatialReference.Gsr1(source, 12, 9, 20, 15, 1.0f))
            Assert.True(Vector4.Distance(p, colour) < 1.0f / 255.0f);
    }
}
