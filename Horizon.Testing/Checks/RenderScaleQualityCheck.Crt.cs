using System;
using System.Numerics;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.Scaling;

namespace Horizon.Testing.Checks;

internal sealed partial class RenderScaleQualityCheck
{
    private readonly bool _checkCrt;
    private CrtEffect _crt = null!;
    private readonly CrtContextProbe _crtProbe = new();
    private Vector2 _crtStartSize;

    private void ConfigureCrt(Renderer2D renderer)
    {
        renderer.PostProcessing.Add(_crtProbe);
        _crt = renderer.PostProcessing.Add(new CrtEffect { PixelSize = PIXEL });
    }

    private void CrtScript()
    {
        _crtStartSize = Engine.WindowManager.Display.WindowSize;
        Frames(20);
        Step(() => { CrtSampling(); return true; });
        CrtScales("wide", 1);
        CrtScales("zoom", 0.5f);
        if ((Engine.WindowManager.Features & Horizon.Platform.PlatformFeatures.DisplaySettings) != 0)
        {
            Step(() =>
            {
                Engine.WindowManager.Apply(Engine.WindowManager.Display with { WindowSize = new Vector2(1200, 900) });
                return true;
            });
            Frames(20);
            CrtScales("four-three", 1);
            Step(() =>
            {
                Engine.WindowManager.Apply(Engine.WindowManager.Display with { WindowSize = new Vector2(900, 1000) });
                return true;
            });
            Frames(20);
            CrtScales("portrait", 0.5f);
        }
        Step(() => { _crt.Resolution = new Vector2(320, 180); return true; });
        CrtScales("explicit", 1);
        Step(() =>
        {
            Engine.WindowManager.Scaling.Settings = RenderScaleSettings.Native;
            Engine.WindowManager.Apply(Engine.WindowManager.Display with { WindowSize = _crtStartSize });
            _checks.Report();
            _finished = true;
            return true;
        });
    }

    private void CrtScales(string label, float zoom)
    {
        foreach (float scale in new[] { 1.0f, 0.75f, 0.45f })
        {
            Step(() =>
            {
                // The same mapping as Fighter: one world unit is a two-logical-pixel art dot at zoom 1.
                _camera.ViewSize = Engine.WindowManager.ViewportSize / PIXEL;
                _camera.Zoom = zoom;
                _crt.PixelSize = PIXEL / zoom;
                Engine.WindowManager.Scaling.Settings = scale == 1 ? RenderScaleSettings.Native : RenderScaleSettings.At(scale);
                return true;
            });
            Frames(12);
            Step(() => { _capture = Engine.CapturePixels(); return true; });
            Step(() => _capture!.IsCompleted);
            Step(() =>
            {
                ImagePixels image = _capture!.Result;
                Vector2 logical = Engine.WindowManager.ViewportSize;
                RenderScalePlan plan = Engine.WindowManager.Scaling.Plan;
                Vector2 wanted = _crt.Resolution ?? logical * (zoom / PIXEL);
                wanted = new Vector2(MathF.Round(wanted.X), MathF.Round(wanted.Y));
                Vector2 tube = _crt.ResolveResolution(_crtProbe.Logical);
                string name = $"CRT {label} {scale:0.00}";
                Check($"{name}, logical picture follows the window", () => _crtProbe.Logical == logical);
                Check($"{name}, source texture follows scene scale", () => _crtProbe.Source == new Vector2(plan.SceneWidth, plan.SceneHeight));
                Check($"{name}, tube grid follows artwork/zoom or explicit override", () => tube == wanted);
                Check($"{name}, final image has native dimensions and visible content", () => image.Width == (int)logical.X && image.Height == (int)logical.Y && Brightness(image, 0.15, 0.9) > 0.025);
                Horizon.Logging.Log.Info($"[CRT] {name}: source {_crtProbe.Source}, logical {_crtProbe.Logical}, tube {tube}, output {image.Width}x{image.Height}, zoom {zoom}.");
                if (Environment.GetEnvironmentVariable(CAPTURES) is { Length: > 0 } folder)
                    ImageFile.SavePng(System.IO.Path.Combine(folder, $"crt-{label}-{scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}.png"), image.Pixels, image.Width, image.Height);
                return true;
            });
        }
    }

    private sealed class CrtContextProbe : PostEffect
    {
        public Vector2 Logical, Source;
        protected override void Render(PostContext context)
        {
            Logical = context.LogicalSize;
            Source = context.SourceSize;
            context.Copy();
        }
    }

    // Read the actual shader, with non-integer dot/output ratios that would beat visibly without filtering.
    private void CrtSampling()
    {
        foreach (Vector2 warp in new[] { Vector2.Zero, new Vector2(1.0f / 32, 1.0f / 24) })
        foreach (float scale in new[] { 1.0f, 0.75f, 0.45f })
        {
            const int w = 640, h = 384;
            int ow = (int)(w * scale), oh = (int)(h * scale);
            byte[] pixels = new byte[ow * oh * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 96;
                pixels[i + 3] = 255;
            }
            using Texture source = Texture.FromPixels((uint)ow, (uint)oh, pixels, TextureDefinition.RgbaUnsignedByte);
            using var target = new PostTarget((uint)ow, (uint)oh);
            using var processor = new PostProcessor();
            // Four output samples per dot at native, three at 75%, 1.8 at 45%.
            processor.Add(new CrtEffect { PixelSize = 4, Warp = warp });
            RenderTarget? bound = Engine.Graphics.BoundRenderTarget;
            var before = RenderState.Save();
            byte[] actual = new byte[pixels.Length];
            try
            {
                RenderState.Blend = false;
                RenderState.DepthTest = false;
                processor.Prepare();
                processor.Run(new Vector2(ow, oh), 0, source, static () => { }, target.Bind, target.Size, new Vector2(w, h));
                target.Bind();
                Engine.Graphics.ReadPixels(0, 0, (uint)ow, (uint)oh, actual);
            }
            finally
            {
                Engine.Graphics.BindRenderTarget(bound);
                RenderState.Restore(before);
            }
            int low = 255, high = 0;
            double sum = 0;
            int count = 0;
            // Exclude the deliberately black glass border.
            for (int y = oh / 4; y < oh * 3 / 4; y++)
            for (int x = ow / 4; x < ow * 3 / 4; x++)
            {
                int value = actual[(y * ow + x) * 4];
                low = Math.Min(low, value);
                high = Math.Max(high, value);
                sum += value;
                count++;
            }
            Horizon.Logging.Log.Info($"[CRT] Flat field scale {scale}, warp {warp}: central range {low}..{high}, mean {sum / count:0.000}/255.");
            if (scale < 0.5f)
            {
                Check($"CRT undersampled flat field has no periodic beam modulation, warp {warp}", () => high - low <= 1);
                Check($"CRT undersampled flat field keeps its brightness, warp {warp}", () => Math.Abs(sum / count - 96) <= 1);
            }
            else if (scale == 1)
                Check($"CRT resolvable scanlines remain visible, warp {warp}", () => high - low > 12);
        }
    }
}
