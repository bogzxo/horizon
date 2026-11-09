using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Scaling;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Testing.Checks;

/// <summary>
/// Checks the render scale on a real GPU. The world on the left is fine lines and dots in a renderer that follows the
/// window, the right is text in a UI laid out against it. It goes through native, 0.75 and 0.5 with CAS, nearest and
/// bilinear, a scale CAS can't do, the whole frame drawn small, a resize and the surface going away and coming back,
/// checking every time how big the scene really is drawn, that the window comes out at its own size, and that the
/// camera, the UI's layout and where a point of the window is in the world never move. The pixels are read back and
/// how sharp each half is gets measured, the world softer at a lower scale, the UI as sharp as at full size unless
/// the whole frame is drawn small. CAS and nearest run on the GPU over a picture of known texels as well and are held
/// to <see cref="CasReference"/> and to what nearest has to be. The upscale's GPU time is put in the log for each.
/// </summary>
internal sealed class RenderScaleCheck : Scene2D, ISelfCheck
{
    private readonly TestChecks _checks = new("RenderScale");
    private readonly Queue<Func<bool>> _steps = new();
    private readonly Dictionary<string, Measured> _measured = [];

    private volatile bool _finished;
    private volatile int _count, _failed, _skipped;

    public bool Finished => _finished;
    public int Failed => _failed;
    public int Count => _count;
    public int Skipped => _skipped;

    private Label _text = null!;
    private int _waitFrames;
    private Task<ImagePixels>? _capture;
    private Vector2 _native, _startSize;
    private bool _resizes;

    // What stays put whatever the scale, as it was at native
    private Vector2 _cameraView, _worldPoint;
    private Vector2? _probe;
    private float _uiScale;
    private UIRect _textBounds;

    // The upscale's GPU time while a measurement runs
    private double _upscaleMs, _frameMs;
    private int _timedFrames;

    private readonly record struct Measured(int Width, int Height, double World, double Ui, ScaledFrame Frame, RenderScalePlan Plan, double UpscaleMs, double FrameMs, double Error, double UiError);

    // The frame at native, what every scaled one is held to for how far its world is from it
    private ImagePixels? _nativeImage;

    public override void Initialize()
    {
        base.Initialize();

        ClearWorld();
        var module = UI.CreateModule();
        _text = module.AddComponent(new Label("Render scale\nthe quick brown fox\njumps over the lazy dog\n0123456789 #%&@")
        {
            Anchor = Origin.TopRight,
            Position = new Vector2(-32, -32),
            TextScale = 0.45f
        });

        _native = Engine.WindowManager.ViewportSize;
        _startSize = Engine.WindowManager.Display.WindowSize;
        _resizes = (Engine.WindowManager.Features & Horizon.Platform.PlatformFeatures.DisplaySettings) != 0;
        Engine.Graphics.ClearColor = new Vector4(0.08f, 0.09f, 0.12f, 1.0f);
        Renderer.ClearColor = new Vector4(0.08f, 0.09f, 0.12f, 1.0f);

        Script();
    }

    // Fine lines and dots over the left of the window, the world's half
    private void ClearWorld()
    {
        Shapes.Describe = shapes =>
        {
            Vector2 view = Camera.ViewSize;
            float left = -view.X * 0.5f + 16.0f, right = -view.X * 0.1f, top = view.Y * 0.5f - 16.0f, bottom = -view.Y * 0.5f + 16.0f;
            for (float x = left; x < right; x += 6.0f)
                shapes.Line(new Vector2(x, bottom), new Vector2(x + 40.0f, top), 1.0f, new Vector3(0.9f, 0.8f, 0.3f));
            for (float y = bottom; y < top; y += 9.0f)
            {
                for (float x = left; x < right; x += 9.0f)
                    shapes.FillCircle(new Vector2(x, y), 1.5f, new Vector3(0.3f, 0.8f, 1.0f));
            }
        };
    }

    private void Script()
    {
        Frames(20);
        Step(() =>
        {
            _cameraView = Camera.ViewSize;
            _uiScale = UI.UIScale;
            _worldPoint = Camera.ScreenToWorld(new Vector2(123.0f, 77.0f));
            _textBounds = _text.Bounds;

            // A pixel of the window the text is under, found once at the size things started at
            for (float y = 4.0f; y < _native.Y * 0.5f && _probe is null; y += 6.0f)
            {
                for (float x = _native.X * 0.5f; x < _native.X; x += 6.0f)
                {
                    if (ReferenceEquals(UI.FindAtWindow(new Vector2(x, y)), _text))
                    {
                        _probe = new Vector2(x, y);
                        break;
                    }
                }
            }

            // What the screen is, and whether the plan the frame started with is what the platform's policy says for it
            var scaling = Engine.WindowManager.Scaling;
            RenderScalePlan started = scaling.Plan;
            RenderScalePlan expected = RenderScalePlan.For(scaling.Settings, Engine.Graphics.SwapchainWidth, Engine.Graphics.SwapchainHeight, Engine.WindowManager.DisplayScale, Engine.Graphics.MaxTextureSize);
            Horizon.Logging.Log.Info($"[RenderScale] At the start, window {_native.X}x{_native.Y} px, swapchain {Engine.Graphics.SwapchainWidth}x{Engine.Graphics.SwapchainHeight}, display scale (density) {Engine.WindowManager.DisplayScale}, so {_native.X / Engine.WindowManager.DisplayScale:0}x{_native.Y / Engine.WindowManager.DisplayScale:0} dp, UI scale {UI.UIScale}, text found at {_probe}. {FrameScaler.Describe(started)}");
            Check("the plan the run started with is what its settings say for this surface and density", () => started == expected);
            Check("a pixel the text is under is found", () => _probe is not null);
            Check("native is what there is unless somebody asks", () => !Engine.WindowManager.Scaling.Plan.Active || Engine.WindowManager.Scaling.Settings.Policy != RenderScalePolicy.Native);
            return true;
        });

        Measure("native", RenderScaleSettings.Native);
        Measure("cas 0.75", RenderScaleSettings.At(0.75f));
        Measure("cas 0.5", RenderScaleSettings.At(0.5f));
        Measure("cas 0.5 sharp", RenderScaleSettings.At(0.5f) with { Sharpness = 1.0f });
        Measure("bilinear 0.5", RenderScaleSettings.At(0.5f) with { Upscaler = Upscaler.Bilinear });
        Measure("nearest 0.5", RenderScaleSettings.At(0.5f) with { Upscaler = Upscaler.Nearest });
        Measure("cas 0.4", RenderScaleSettings.At(0.4f));
        Measure("bilinear past cas 0.4", RenderScaleSettings.At(0.4f) with { BeyondLimit = UpscaleLimit.Bilinear });
        Measure("auto", RenderScaleSettings.Auto);
        Measure("gsr1 0.5", RenderScaleSettings.At(0.5f) with { Upscaler = Upscaler.Gsr1 });
        Measure("fsr1 0.5", RenderScaleSettings.At(0.5f) with { Upscaler = Upscaler.Fsr1 });
        Measure("fsr1 0.4", RenderScaleSettings.At(0.4f) with { Upscaler = Upscaler.Fsr1 });
        Measure("gsr1 0.4", RenderScaleSettings.At(0.4f) with { Upscaler = Upscaler.Gsr1 });
        Measure("whole 0.5", RenderScaleSettings.At(0.5f) with { Composition = RenderComposition.WholeFrame });
        Measure("whole 0.75", RenderScaleSettings.At(0.75f) with { Composition = RenderComposition.WholeFrame });

        Step(() =>
        {
            Measured native = _measured["native"];
            Check("native draws nothing extra", () => !native.Plan.Active && native.Frame == ScaledFrame.Native);

            foreach (string name in new[] { "cas 0.75", "cas 0.5", "cas 0.5 sharp", "bilinear 0.5", "nearest 0.5", "cas 0.4", "bilinear past cas 0.4", "whole 0.5", "whole 0.75", "gsr1 0.5", "fsr1 0.5", "fsr1 0.4", "gsr1 0.4" })
            {
                Measured scaled = _measured[name];
                Check($"{name}, the window comes out at its own size", () => scaled.Width == (int)_native.X && scaled.Height == (int)_native.Y);
                Check($"{name}, the world is drawn with fewer pixels", () => scaled.World < native.World * 0.97);
            }

            Check("cas 0.75 is CAS at 0.75", () => _measured["cas 0.75"].Plan is { Effective: Upscaler.Cas, Active: true } plan && Near(plan.EffectiveScale, 0.75f));
            Check("cas 0.4 is past CAS's reach and is CAS after a stretch to half the window", () => _measured["cas 0.4"].Plan is { Effective: Upscaler.Cas, HasPrepass: true, Fallback: null } plan && (ulong)_native.X * (ulong)_native.Y <= 4UL * plan.PrepassWidth * plan.PrepassHeight);
            // Which upscaler comes closest to the native picture is measured and logged, not checked. The world here is lines and
            // dots finer than a pixel at a lower scale, which alias as the scene is drawn whatever upscales it after. It is
            // there to see the aliasing, see check-render-scale-quality for scenes that look like a game
            Check("bilinear past CAS's reach is used when asked for and says so", () => _measured["bilinear past cas 0.4"].Plan is { Effective: Upscaler.Bilinear, HasPrepass: false, Fallback: not null });
            Check("auto is 720p in the window's shape", () => _measured["auto"].Plan is { } auto && (auto.Active ? Math.Min(auto.SceneWidth, auto.SceneHeight) == 720 : Math.Min(_native.X, _native.Y) <= 720) && _measured["auto"].Width == (int)_native.X);
            Check("nearest is nearest", () => _measured["nearest 0.5"].Plan.Effective == Upscaler.Nearest);
            Check("gsr1 and fsr1 at 0.5 are what was asked for in one go", () => _measured["gsr1 0.5"].Plan is { Effective: Upscaler.Gsr1, HasPrepass: false } && _measured["fsr1 0.5"].Plan is { Effective: Upscaler.Fsr1, HasPrepass: false });
            Check("gsr1 scales directly at 0.4; fsr1 stays within EASU's documented reach using a prepass", () => _measured["gsr1 0.4"].Plan is { Effective: Upscaler.Gsr1, HasPrepass: false, Fallback: null } && _measured["fsr1 0.4"].Plan is { Effective: Upscaler.Fsr1, HasPrepass: true, Fallback: null });
            Check("more sharpness is a sharper world", () => _measured["cas 0.5 sharp"].World > _measured["cas 0.5"].World);

            // The UI at the window's own pixels is as sharp as it was, drawn small with the rest it isn't
            Check("the UI over a scaled scene is drawn at full size", () => _measured["cas 0.5"].Frame == ScaledFrame.Overlaid && Within(_measured["cas 0.5"].Ui, native.Ui, 0.03));
            Check("the UI over a 0.75 scene is drawn at full size", () => Within(_measured["cas 0.75"].Ui, native.Ui, 0.03));
            // The UI side of the window has nothing of the scene in it, so drawn natively over the scene it is the native
            // frame's to the pixel, and drawn small with the whole frame it isn't. How much sharper or softer it gets goes by
            // how big the UI is and how hard the upscaler sharpens, so that's in the log and not checked
            Check("the UI over a scaled scene is the native UI to the pixel", () => _measured["cas 0.5"].UiError <= 0.001 && _measured["cas 0.75"].UiError <= 0.001);
            Check("the whole frame drawn small takes the UI with it", () => _measured["whole 0.5"].Frame == ScaledFrame.Whole && _measured["whole 0.75"].Frame == ScaledFrame.Whole && _measured["whole 0.5"].UiError > 0.001 && _measured["whole 0.75"].UiError > 0.001);
            return true;
        });

        GpuParity();
        CustomOutput();
        Resize();
        SurfaceCycle();

        Step(() =>
        {
            Engine.WindowManager.Scaling.Settings = RenderScaleSettings.Native;
            return true;
        });
        Frames(6);
        Step(() =>
        {
            Check("back at native there is no scene target", () => !Engine.WindowManager.Scaling.Plan.Active && Engine.Graphics.WindowStandIn is null);
            Check("back at native the renderer is the window's size", () => Renderer.FrameBuffer.Width == (uint)Engine.WindowManager.ViewportSize.X);
            foreach (var (name, m) in _measured)
                Horizon.Logging.Log.Info($"[RenderScale] {name}, {m.Plan.SceneWidth}x{m.Plan.SceneHeight} to {m.Width}x{m.Height} with {m.Plan.Effective}, {m.Frame}, world sharpness {m.World:0.0000}, UI sharpness {m.Ui:0.0000}, world error against native {m.Error:0.0000}, UI error against native {m.UiError:0.0000}, upscale {m.UpscaleMs:0.000} ms, GPU frame {m.FrameMs:0.000} ms");
            _checks.Report();
            _finished = true;
            return true;
        });
    }

    /// <summary>Helper method to set a scale, let it settle, check what stays put and read the frame back.</summary>
    private void Measure(string name, RenderScaleSettings settings)
    {
        Step(() =>
        {
            Engine.WindowManager.Scaling.Settings = settings;
            return true;
        });
        Frames(8);
        Step(() =>
        {
            RenderScalePlan plan = Engine.WindowManager.Scaling.Plan;
            Check($"{name}, the renderer is the scene's size", () => Renderer.FrameBuffer.Width == plan.SceneWidth && Renderer.FrameBuffer.Height == plan.SceneHeight);
            Check($"{name}, the camera and the UI scale are what they were", () => Camera.ViewSize == _cameraView && UI.UIScale == _uiScale);
            Check($"{name}, a point of the window is where it was in the world", () => Vector2.Distance(Camera.ScreenToWorld(new Vector2(123.0f, 77.0f)), _worldPoint) < 0.01f);
            Check($"{name}, the UI is laid out where it was", () => _text.Bounds.Equals(_textBounds));
            Check($"{name}, the pixel the text was under still finds it", () => _probe is { } probe && ReferenceEquals(UI.FindAtWindow(probe), _text));
            Check($"{name}, the world side finds no text", () => !ReferenceEquals(UI.FindAtWindow(new Vector2(_native.X * 0.2f, _native.Y * 0.5f)), _text));
            (_upscaleMs, _frameMs, _timedFrames) = (0.0, 0.0, 0);
            return true;
        });

        // The GPU says a frame or two late, so a few are timed
        for (int i = 0; i < 12; i++)
            Step(() =>
            {
                foreach (GpuScope scope in Engine.Graphics.GpuScopes)
                {
                    if (scope.Name == FrameScaler.GPU_SCOPE)
                        _upscaleMs += scope.Milliseconds;
                }
                _frameMs += Engine.Graphics.GpuFrameMilliseconds;
                _timedFrames++;
                return true;
            });

        Step(() =>
        {
            _capture = Engine.CapturePixels();
            return true;
        });
        Step(() => _capture!.IsCompleted);
        Step(() =>
        {
            ImagePixels image = _capture!.Result;
            RenderScalePlan plan = Engine.WindowManager.Scaling.Plan;
            double world = Sharpness(image, 0.02, 0.38), ui = Sharpness(image, 0.62, 0.98, top: 0.0, bottom: 0.5);
            if (!plan.Active)
                _nativeImage ??= image;
            double error = _nativeImage is { } reference && reference.Width == image.Width && reference.Height == image.Height ? Error(image, reference, 0.02, 0.38) : double.NaN;
            double uiError = _nativeImage is { } nativeUi && nativeUi.Width == image.Width && nativeUi.Height == image.Height ? Error(image, nativeUi, 0.62, 0.98, top: 0.0, bottom: 0.3) : double.NaN;
            _measured[name] = new Measured(image.Width, image.Height, world, ui, Engine.WindowManager.Scaling.LastFrame, plan, _upscaleMs / Math.Max(1, _timedFrames), _frameMs / Math.Max(1, _timedFrames), error, uiError);
            return true;
        });
    }

    private void CustomOutput()
    {
        var custom = new SolidUpscaler();
        Step(() =>
        {
            Engine.WindowManager.Scaling.Settings = RenderScaleSettings.At(0.5f) with
            {
                Upscaler = Upscaler.Custom,
                Custom = custom,
                Composition = RenderComposition.WholeFrame
            };
            return true;
        });
        Frames(8);
        Step(() =>
        {
            _capture = Engine.CapturePixels();
            return true;
        });
        Step(() => _capture!.IsCompleted);
        Step(() =>
        {
            ImagePixels image = _capture!.Result;
            int pixel = ((image.Height / 2) * image.Width + image.Width / 4) * 4;
            Check("the custom provider draws the scaled frame", () => custom.Calls > 0 && Engine.WindowManager.Scaling.Plan.Effective == Upscaler.Custom);
            Check("the custom provider's output is not overwritten by CAS", () => image.Pixels[pixel] == 255 && image.Pixels[pixel + 1] == 0 && image.Pixels[pixel + 2] == 255);
            Engine.WindowManager.Scaling.Settings = RenderScaleSettings.Native;
            return true;
        });
        Frames(6);
    }

    private sealed class SolidUpscaler : ISpatialUpscaler
    {
        public string Name => "solid output regression";
        public int Calls { get; private set; }
        public bool Supports(uint sceneWidth, uint sceneHeight, uint outputWidth, uint outputHeight) => true;

        public void Upscale(in SpatialUpscale pass)
        {
            Calls++;
            GraphicsDevice graphics = GraphicsDevice.Current;
            Vector4 before = graphics.ClearColor;
            graphics.ClearColor = new Vector4(1, 0, 1, 1);
            graphics.Clear(ClearTargets.Color);
            graphics.ClearColor = before;
        }
    }

    /// <summary>Helper method to run CAS and nearest over a picture of known texels and hold them to what they have to be.</summary>
    private void GpuParity()
    {
        Step(() =>
        {
            foreach ((int w, int h, int ow, int oh, float sharpness) in new[] { (16, 12, 32, 24, 0.0f), (16, 12, 32, 24, 1.0f), (12, 9, 16, 12, 0.5f), (15, 10, 16, 12, 0.0f) })
            {
                byte[] texels = Pattern(w, h);
                byte[] gpu = RunPass(Upscaler.Cas, texels, w, h, ow, oh, sharpness);

                var source = new Vector3[w * h];
                for (int i = 0; i < source.Length; i++)
                    source[i] = new Vector3(texels[i * 4], texels[i * 4 + 1], texels[i * 4 + 2]) / 255.0f;
                Vector3[] reference = CasReference.Upscale(source, w, h, ow, oh, sharpness);

                int worst = 0;
                for (int i = 0; i < reference.Length; i++)
                {
                    worst = Math.Max(worst, Math.Abs(gpu[i * 4] - (int)MathF.Round(reference[i].X * 255.0f)));
                    worst = Math.Max(worst, Math.Abs(gpu[i * 4 + 1] - (int)MathF.Round(reference[i].Y * 255.0f)));
                    worst = Math.Max(worst, Math.Abs(gpu[i * 4 + 2] - (int)MathF.Round(reference[i].Z * 255.0f)));
                }

                Horizon.Logging.Log.Info($"[RenderScale] CAS on the GPU against the reference, {w}x{h} to {ow}x{oh} at sharpness {sharpness}, at most {worst} of 255 apart.");
                Check($"CAS on the GPU is the reference, {w}x{h} to {ow}x{oh} at sharpness {sharpness.ToString(CultureInfo.InvariantCulture)}", () => worst <= 2);

                byte[] nearest = RunPass(Upscaler.Nearest, texels, w, h, ow, oh, 0.0f);
                bool exact = true;
                for (int y = 0; y < oh && exact; y++)
                {
                    for (int x = 0; x < ow; x++)
                    {
                        int sx = Math.Min(w - 1, (int)MathF.Floor((x + 0.5f) * (w / (float)ow)));
                        int sy = Math.Min(h - 1, (int)MathF.Floor((y + 0.5f) * (h / (float)oh)));
                        int at = (y * ow + x) * 4, from = (sy * w + sx) * 4;
                        if (nearest[at] != texels[from] || nearest[at + 1] != texels[from + 1] || nearest[at + 2] != texels[from + 2])
                        {
                            exact = false;
                            break;
                        }
                    }
                }

                Check($"nearest on the GPU is every pixel's own texel, {w}x{h} to {ow}x{oh}", () => exact);
            }

            // GSR 1 and FSR 1 against their CPU twins. FSR 1 gathers and loads texels as they are, GSR 1 also samples the
            // centre linearly, which a GPU weighs with fewer bits than a float, so it's held to most pixels within a few 255ths
            foreach ((int w, int h, int ow, int oh, float sharpness) in new[] { (16, 12, 32, 24, 0.0f), (16, 12, 32, 24, 1.0f), (12, 9, 16, 12, 0.5f), (15, 10, 16, 12, 0.0f), (20, 12, 24, 15, 0.25f) })
            {
                byte[] texels = Pattern(w, h);
                var source = new Vector4[w * h];
                for (int i = 0; i < source.Length; i++)
                    source[i] = new Vector4(texels[i * 4], texels[i * 4 + 1], texels[i * 4 + 2], texels[i * 4 + 3]) / 255.0f;

                string size = $"{w}x{h} to {ow}x{oh} at sharpness {sharpness.ToString(CultureInfo.InvariantCulture)}";
                var (fsrWorst, fsrNear) = Apart(RunPass(Upscaler.Fsr1, texels, w, h, ow, oh, sharpness), SpatialReference.Fsr1(source, w, h, ow, oh, sharpness));
                var (gsrWorst, gsrNear) = Apart(RunPass(Upscaler.Gsr1, texels, w, h, ow, oh, sharpness), SpatialReference.Gsr1(source, w, h, ow, oh, sharpness));
                Horizon.Logging.Log.Info($"[RenderScale] On the GPU against the reference, {size}, FSR 1 at most {fsrWorst} of 255 apart ({fsrNear:P1} within 3), GSR 1 at most {gsrWorst} ({gsrNear:P1} within 3).");
                Check($"FSR 1 on the GPU is the reference, {size}", () => fsrWorst <= 3);
                Check($"GSR 1 on the GPU is the reference, {size}", () => gsrNear >= 0.98);
            }

            // GSR1 has no CAS/EASU 4x-area restriction: exercise the tablet's 0.45 ratio directly.
            foreach (float sharpness in new[] { 0.0f, 1.0f })
            {
                const int w = 18, h = 9, ow = 40, oh = 20;
                byte[] texels = Pattern(w, h);
                var source = new Vector4[w * h];
                for (int i = 0; i < source.Length; i++)
                    source[i] = new Vector4(texels[i * 4], texels[i * 4 + 1], texels[i * 4 + 2], texels[i * 4 + 3]) / 255.0f;
                byte[] gsr = RunPass(Upscaler.Gsr1, texels, w, h, ow, oh, sharpness);
                var (worst, near) = Apart(gsr, SpatialReference.Gsr1(source, w, h, ow, oh, sharpness));
                Horizon.Logging.Log.Info($"[RenderScale] Direct GSR 1 at 0.45, sharpness {sharpness}, at most {worst} of 255 apart ({near:P1} within 3).");
                Check($"GSR 1 direct 0.45 matches reference, sharpness {sharpness}", () => near >= 0.98);
                byte[] linear = RunPass(Upscaler.Bilinear, texels, w, h, ow, oh, sharpness);
                int correction = 0;
                for (int i = 0; i < gsr.Length; i++)
                    if (i % 4 != 3) correction = Math.Max(correction, Math.Abs(gsr[i] - linear[i]));
                // 23/255 is the upstream high-contrast correction cap; allow two bytes for sampler/output rounding.
                Check($"GSR 1 bounds high-contrast correction, sharpness {sharpness} (max {correction}/255)", () => correction <= 25);
            }

            return true;
        });
    }

    // Helper method to say how far a picture from the GPU is from a reference, the most any channel is out by in 255ths and
    // the share of channels within 3 of it
    private static (int Worst, double Near) Apart(byte[] gpu, Vector4[] reference)
    {
        int worst = 0, near = 0, count = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            Vector4 r = reference[i];
            foreach ((int at, float value) in new[] { (i * 4, r.X), (i * 4 + 1, r.Y), (i * 4 + 2, r.Z), (i * 4 + 3, r.W) })
            {
                int apart = Math.Abs(gpu[at] - (int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f));
                worst = Math.Max(worst, apart);
                near += apart <= 3 ? 1 : 0;
                count++;
            }
        }

        return (worst, count == 0 ? 1.0 : near / (double)count);
    }

    // Helper method to draw one of the upscalers from a picture into a target of the output's size and read it, top row first
    private byte[] RunPass(Upscaler upscaler, byte[] texels, int w, int h, int ow, int oh, float sharpness)
    {
        GraphicsDevice device = Engine.Graphics;
        Texture picture = Texture.FromPixels((uint)w, (uint)h, texels, TextureDefinition.RgbaUnsignedByte);
        RenderTarget target = RenderTarget.Create(RenderTargetDescription.Color((uint)ow, (uint)oh));
        RenderTarget? bound = device.BoundRenderTarget;
        var before = RenderState.Save();

        try
        {
            target.Bind();
            RenderState.Blend = false;
            RenderState.DepthTest = false;
            Engine.WindowManager.Scaling.DrawPass(upscaler, picture, (uint)w, (uint)h, (uint)ow, (uint)oh, sharpness);

            var bottomUp = new byte[ow * oh * 4];
            device.ReadPixels(0, 0, (uint)ow, (uint)oh, bottomUp);
            var upright = new byte[bottomUp.Length];
            for (int y = 0; y < oh; y++)
                Buffer.BlockCopy(bottomUp, y * ow * 4, upright, (oh - 1 - y) * ow * 4, ow * 4);
            return upright;
        }
        finally
        {
            device.BindRenderTarget(bound);
            RenderState.Restore(before);
            target.Dispose();
            picture.Dispose();
        }
    }

    // Texels with edges, flat runs and noise, the same every run
    private static byte[] Pattern(int w, int h)
    {
        var random = new Random(1234);
        var texels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool edge = x < w / 3;
                bool flat = y < h / 3;
                texels[i] = (byte)(edge ? 230 : flat ? 40 : random.Next(256));
                texels[i + 1] = (byte)(edge ? 20 : flat ? 40 : random.Next(256));
                texels[i + 2] = (byte)(((x + y) & 1) == 0 ? 255 : 0);
                texels[i + 3] = 255;
            }
        }

        return texels;
    }

    /// <summary>Helper method to have the window another size while the scene is drawn small, where the platform can.</summary>
    private void Resize()
    {
        if (!_resizes)
        {
            _checks.Skip("a resize while scaled", "the platform can't resize its window (no PlatformFeatures.DisplaySettings)");
            _skipped++;
            return;
        }

        Step(() =>
        {
            Engine.WindowManager.Scaling.Settings = RenderScaleSettings.At(0.5f);
            Engine.WindowManager.Apply(Engine.WindowManager.Display with { WindowSize = new Vector2(1280, 720) });
            return true;
        });
        Frames(20);
        Step(() =>
        {
            Vector2 now = Engine.WindowManager.ViewportSize;
            RenderScalePlan plan = Engine.WindowManager.Scaling.Plan;
            Check("after a resize the plan is of the new size", () => plan.OutputWidth == (uint)now.X && plan.OutputHeight == (uint)now.Y);
            Check("after a resize the scene is half of it", () => plan.SceneWidth == (uint)MathF.Round(now.X * 0.5f, MidpointRounding.AwayFromZero) && Renderer.FrameBuffer.Width == plan.SceneWidth);
            Check("after a resize the camera follows the window, not the scene", () => Camera.ViewSize == now);
            Engine.WindowManager.Apply(Engine.WindowManager.Display with { WindowSize = _startSize });
            return true;
        });
        Frames(20);
    }

    /// <summary>Helper method to take the surface away and give it back while the scene is drawn small.</summary>
    private void SurfaceCycle()
    {
        Task<bool>? detaching = null;
        Task<SurfaceAttachResult>? attaching = null;
        uint sceneWidth = 0;

        Step(() =>
        {
            Engine.WindowManager.Scaling.Settings = RenderScaleSettings.At(0.5f);
            return true;
        });
        Frames(8);
        Step(() =>
        {
            sceneWidth = Engine.WindowManager.Scaling.Plan.SceneWidth;
            detaching = Engine.WindowManager.RequestSurfaceDetach();
            return true;
        });
        Step(() => detaching!.IsCompleted);
        Frames(6);
        Step(() =>
        {
            Check("without a surface nothing stands in for the window", () => !Engine.Graphics.HasWindowImage && Engine.Graphics.WindowStandIn is null);
            Check("without a surface the renderer keeps the scene's size", () => Renderer.FrameBuffer.Width == sceneWidth && Engine.WindowManager.Scaling.TargetSize.X == sceneWidth);
            Check("without a surface the frames are native ones", () => Engine.WindowManager.Scaling.LastFrame == ScaledFrame.Native);
            attaching = Engine.WindowManager.RequestSurfaceAttach();
            return true;
        });
        Step(() => attaching!.IsCompleted);
        Frames(10);
        Step(() =>
        {
            Check("with the surface back the scene is drawn small and overlaid again", () => Engine.Graphics.HasWindowImage && Engine.WindowManager.Scaling.LastFrame == ScaledFrame.Overlaid);
            _capture = Engine.CapturePixels();
            return true;
        });
        Step(() => _capture!.IsCompleted);
        Step(() =>
        {
            Check("with the surface back the window comes out at its own size", () => _capture!.Result.Width == (int)Engine.WindowManager.ViewportSize.X);
            return true;
        });
    }

    /// <summary>
    /// How sharp a stretch of the picture is, the mean of how much the brightness changes from a pixel to the next,
    /// across and down. Between fractions of the width, and of the height from the top.
    /// </summary>
    private static double Sharpness(in ImagePixels image, double from, double to, double top = 0.0, double bottom = 1.0)
    {
        int x0 = (int)(image.Width * from), x1 = (int)(image.Width * to);
        int y0 = (int)(image.Height * top), y1 = (int)(image.Height * bottom);
        double sum = 0.0;
        long count = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(image.Height - 1, y1); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(image.Width - 1, x1); x++)
            {
                double here = Luma(image, x, y);
                sum += Math.Abs(Luma(image, x + 1, y) - here) + Math.Abs(Luma(image, x, y + 1) - here);
                count++;
            }
        }

        return count == 0 ? 0.0 : sum / count;
    }

    /// <summary>How far a stretch of a picture is from the same stretch of another, the mean of how much their brightness differs a pixel.</summary>
    private static double Error(in ImagePixels image, in ImagePixels reference, double from, double to, double top = 0.0, double bottom = 1.0)
    {
        int x0 = (int)(image.Width * from), x1 = (int)(image.Width * to);
        double sum = 0.0;
        long count = 0;
        for (int y = (int)(image.Height * top); y < (int)(image.Height * bottom); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(image.Width, x1); x++)
            {
                sum += Math.Abs(Luma(image, x, y) - Luma(reference, x, y));
                count++;
            }
        }

        return count == 0 ? 0.0 : sum / count;
    }

    private static double Luma(in ImagePixels image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return (0.2126 * image.Pixels[i] + 0.7152 * image.Pixels[i + 1] + 0.0722 * image.Pixels[i + 2]) / 255.0;
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.002f;

    private static bool Within(double value, double reference, double share) => Math.Abs(value - reference) <= reference * share;

    public override void Render(float dt)
    {
        base.Render(dt);

        // One step a frame at most, so whatever a step asked for gets a frame in between
        if (_steps.TryPeek(out Func<bool>? step) && step())
            _steps.Dequeue();
    }

    private void Step(Func<bool> step) => _steps.Enqueue(step);

    private void Frames(int frames) => _steps.Enqueue(() =>
    {
        if (_waitFrames == 0)
            _waitFrames = frames;
        return --_waitFrames == 0;
    });

    private void Check(string name, Func<bool> check)
    {
        _checks.Check(name, check);
        _count = _checks.Count;
        _failed = _checks.Count - _checks.Passed;
    }
}
