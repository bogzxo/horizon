using System.Numerics;
using Horizon.Engine;
using Horizon.Rendering.PostProcessing;

namespace Horizon.Tests;

public class CrtResolutionTests
{
    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.75f)]
    [InlineData(0.45f)]
    public void Tube_grid_uses_logical_picture_not_scene_texture(float scale)
    {
        var context = new PostContext
        {
            LogicalSize = new Vector2(2560, 1600),
            SourceSize = new Vector2(2560, 1600) * scale,
            OutputSize = new Vector2(2560, 1600) * scale
        };
        var crt = new CrtEffect { PixelSize = 2 };
        Assert.Equal(new Vector2(1280, 800), crt.ResolveResolution(context.LogicalSize));
    }

    [Theory]
    [InlineData(1600, 900)]
    [InlineData(1200, 900)]
    [InlineData(900, 1200)]
    public void Resize_preserves_art_dot_size_and_explicit_resolution(int width, int height)
    {
        var crt = new CrtEffect { PixelSize = 2 };
        Assert.Equal(new Vector2(width / 2, height / 2), crt.ResolveResolution(new(width, height)));
        crt.Resolution = new Vector2(320, 240);
        crt.PixelSize = 9;
        Assert.Equal(new Vector2(320, 240), crt.ResolveResolution(new(width, height)));
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    [InlineData(1.25f)]
    public void Tube_dots_track_art_under_the_engine_camera_zoom_convention(float zoom)
    {
        Vector2 window = new(1600, 900);
        var camera = new Camera2D(window / 2) { Zoom = zoom };
        var crt = new CrtEffect { PixelSize = 2 / zoom };
        Vector2 expected = new(MathF.Round(camera.Bounds.Width), MathF.Round(camera.Bounds.Height));
        Assert.Equal(expected, crt.ResolveResolution(window));
    }
}
