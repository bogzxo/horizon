using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;

using Horizon.Engine;
using Horizon.Graphics;
using Horizon.Rendering;
using Horizon.Rendering.Lighting;
using Horizon.Rendering.Scaling;
using Horizon.Rendering.Spriting;
using Horizon.Rendering.Tiling;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.Testing.Examples;

namespace Horizon.Testing.Checks;

/// <summary>
/// Puts the upscalers side by side on a scene that looks like a game, held still so every frame is the same picture.
/// The lit cellar of the lighting example (its tile map with its normal and AO maps, its lamps, no flicker), the blob on
/// a frame of its own, a line of text in the world (a UI inside the lit renderer, drawn small with the rest) and a
/// line of UI over it at the window's pixels. Native, CAS, GSR 1, FSR 1 and bilinear at 0.75, 0.5 and 720p are drawn,
/// read back and compared with the native frame, each said in the log with its GPU time, and saved as pictures when
/// HORIZON_RENDER_SCALE_CAPTURES names a folder.
/// <para>
/// What's checked is what has to hold whatever the picture looks like. Every frame comes out at the window's size from
/// the scene size the plan said, with the upscaler that was asked for (after a stretch past its reach, never silently
/// another), the UI line drawn natively is the same as at native, and the world isn't lost (its brightness is near
/// native's). How close each comes to the native frame is evidence for whoever reads the log, not a race one has to win.
/// </para>
/// </summary>
internal sealed partial class RenderScaleQualityCheck : Scene, ISelfCheck
{
    private const float PIXEL = 2.0f;
    private const string CAPTURES = "HORIZON_RENDER_SCALE_CAPTURES";

    public override Camera ActiveCamera { get; protected set; } = null!;

    private readonly TestChecks _checks = new("RenderScaleQuality");
    private readonly Queue<Func<bool>> _steps = new();
    private readonly List<(string Name, Result Result)> _results = [];

    private volatile bool _finished;
    private volatile int _count, _failed;

    public bool Finished => _finished;
    public int Failed => _failed;
    public int Count => _count;
    public int Skipped => 0;

    private UICompositor _ui = null!;
    private Camera2D _camera = null!;
    private int _waitFrames;
    private Task<ImagePixels>? _capture;
    private ImagePixels? _native;
    private double _upscaleMs, _frameMs;
    private int _timed;

    private readonly record struct Result(RenderScalePlan Plan, int Width, int Height, double Error, double Brightness, double Ui, double UpscaleMs, double FrameMs);

    public RenderScaleQualityCheck(bool crt = false) => _checkCrt = crt;

    public override void Initialize()
    {
        Vector2 window = Engine.WindowManager.ViewportSize;
        _camera = AddEntity(new Camera2D(window / PIXEL));
        ActiveCamera = _camera;

        TileMap map = TileMap.Load(World.CELLAR);
        _camera.Position = new Vector3(map.Min + map.Size / 2.0f, 0.0f);

        var lighting = AddEntity(new DeferredRenderer2D((uint)window.X, (uint)window.Y)
        {
            FollowWindow = true,
            Ambient = World.AmbientOf(map),
            Lighting = LightingMode.Direct,
            ClearColor = new Vector4(0.02f, 0.02f, 0.04f, 1.0f)
        });
        lighting.AddEntity(map);
        if (_checkCrt)
            ConfigureCrt(lighting);
        lighting.Occlusion = map.CreateOcclusion();
        lighting.AmbientOcclusion.Enabled = true;
        map.GeometryOcclusion = true;

        // Still lamps, a flicker would make every frame another picture
        foreach (Light2D lamp in World.AddLights(map, lighting).Values)
            lamp.Flicker = 0.0f;
        lighting.AddLight(new Light2D { Position = map.Min + map.Size * new Vector2(0.35f, 0.55f), Radius = 170.0f, Intensity = 1.1f, Size = 4.0f });

        var sprites = lighting.AddEntity(new SpriteBatch());
        Sprite blob = World.CreateBlob(sprites, 3.0f);
        blob.Transform.Position = map.Min + map.Size * new Vector2(0.45f, 0.5f);
        blob.AnimationManager.AnimateFrames = false;

        // Text in the world, a UI inside the lit renderer, so it's part of the scene and drawn small with it
        var words = lighting.AddEntity(new WorldWords());
        var worldUi = words.AddComponent(new UICompositor(_camera, UICompositor.DEFAULT_SKIN_DIRECTORY, UICompositor.DEFAULT_SKIN_FILE));
        worldUi.CreateModule().AddComponent(new Label("THE CELLAR  0123456789") { Anchor = Origin.Bottom, Position = new Vector2(0, 40), TextScale = 0.3f });

        // And text over it all at the window's pixels
        _ui = AddComponent(UICompositor.ForScreen(UICompositor.DEFAULT_SKIN_DIRECTORY, UICompositor.DEFAULT_SKIN_FILE));
        // On a solid panel over the top left, so what the band there measures is the UI and nothing of the scene
        var band = _ui.CreateModule().AddComponent(new StackPanel
        {
            Anchor = Origin.TopLeft,
            Position = Vector2.Zero,
            Size = new Vector2(window.X * 0.6f, window.Y * 0.12f),
            Color = new Vector4(0.08f, 0.08f, 0.1f, 1.0f),
            Padding = new UIEdges(24)
        });
        band.Add(new Label("Render scale quality, native UI 0123456789") { Anchor = Origin.Left, TextScale = 0.3f });

        base.Initialize();
        if (_checkCrt) CrtScript();
        else Script();
    }

    private void Script()
    {
        Frames(30);
        Measure("native", RenderScaleSettings.Native);
        foreach ((string label, RenderScaleSettings scale) in new[] { ("0.75", RenderScaleSettings.At(0.75f)), ("0.5", RenderScaleSettings.At(0.5f)), ("720p", RenderScaleSettings.Auto) })
        {
            foreach (Upscaler upscaler in new[] { Upscaler.Cas, Upscaler.Gsr1, Upscaler.Fsr1, Upscaler.Bilinear })
                Measure($"{RenderScalePlan.NameOf(upscaler).ToLowerInvariant().Replace(" ", "")} {label}", scale with { Upscaler = upscaler });
        }

        Step(() =>
        {
            Result native = _results[0].Result;
            Check("native draws nothing extra", () => !native.Plan.Active);
            foreach (var (name, r) in _results)
            {
                if (!r.Plan.Active)
                    continue;

                Check($"{name}, the window comes out at its own size", () => r.Width == (int)native.Plan.OutputWidth && r.Height == (int)native.Plan.OutputHeight);
                Check($"{name}, it's what was asked for, after a stretch if past its reach", () => r.Plan.Effective == r.Plan.Requested && r.Plan.Fallback is null
                    && (!RenderScalePlan.HasReach(r.Plan.Effective) || r.Plan.HasPrepass == !CasConstants.SupportsScaling(r.Plan.OutputWidth, r.Plan.OutputHeight, r.Plan.SceneWidth, r.Plan.SceneHeight)));
                Check($"{name}, the world isn't lost", () => Within(r.Brightness, native.Brightness, 0.15));
                Check($"{name}, the UI drawn natively is as it was", () => Within(r.Ui, native.Ui, 0.03));
            }

            foreach (var (name, r) in _results)
                Horizon.Logging.Log.Info($"[RenderScaleQuality] {name}, {r.Plan.SceneWidth}x{r.Plan.SceneHeight}{(r.Plan.HasPrepass ? $" stretched to {r.Plan.PrepassWidth}x{r.Plan.PrepassHeight}" : "")} to {r.Width}x{r.Height} with {r.Plan.Effective}, world error against native {r.Error:0.0000}, upscale {r.UpscaleMs:0.000} ms, GPU frame {r.FrameMs:0.000} ms");

            Engine.WindowManager.Scaling.Settings = RenderScaleSettings.Native;
            _checks.Report();
            _finished = true;
            return true;
        });
    }

    private void Measure(string name, RenderScaleSettings settings)
    {
        Step(() =>
        {
            Engine.WindowManager.Scaling.Settings = settings;
            return true;
        });
        Frames(10);
        Step(() =>
        {
            (_upscaleMs, _frameMs, _timed) = (0.0, 0.0, 0);
            return true;
        });
        for (int i = 0; i < 16; i++)
            Step(() =>
            {
                foreach (GpuScope scope in Engine.Graphics.GpuScopes)
                {
                    if (scope.Name == FrameScaler.GPU_SCOPE)
                        _upscaleMs += scope.Milliseconds;
                }
                _frameMs += Engine.Graphics.GpuFrameMilliseconds;
                _timed++;
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
            if (!plan.Active)
                _native ??= image;

            if (Environment.GetEnvironmentVariable(CAPTURES) is { Length: > 0 } folder)
                ImageFile.SavePng(System.IO.Path.Combine(folder, $"quality-{name.Replace(' ', '-')}.png"), image.Pixels, image.Width, image.Height);

            // The world is everything under the top band the native UI line is in
            double error = _native is { } reference && reference.Width == image.Width && reference.Height == image.Height ? Error(image, reference, 0.15, 1.0) : double.NaN;
            _results.Add((name, new Result(plan, image.Width, image.Height, error, Brightness(image, 0.15, 1.0), Gradient(image, 0.01, 0.09), _upscaleMs / Math.Max(1, _timed), _frameMs / Math.Max(1, _timed))));
            return true;
        });
    }

    // How far a band of rows (fractions of the height from the top) is from the same band of another, mean luma apart a pixel
    private static double Error(in ImagePixels image, in ImagePixels reference, double top, double bottom)
    {
        double sum = 0.0;
        long count = 0;
        for (int y = (int)(image.Height * top); y < (int)(image.Height * bottom); y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                sum += Math.Abs(Luma(image, x, y) - Luma(reference, x, y));
                count++;
            }
        }

        return count == 0 ? 0.0 : sum / count;
    }

    private static double Brightness(in ImagePixels image, double top, double bottom)
    {
        double sum = 0.0;
        long count = 0;
        for (int y = (int)(image.Height * top); y < (int)(image.Height * bottom); y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                sum += Luma(image, x, y);
                count++;
            }
        }

        return count == 0 ? 0.0 : sum / count;
    }

    // How much the brightness changes pixel to pixel over a band of rows, for the native UI line staying as sharp
    private static double Gradient(in ImagePixels image, double top, double bottom)
    {
        double sum = 0.0;
        long count = 0;
        for (int y = (int)(image.Height * top); y < Math.Min(image.Height - 1, (int)(image.Height * bottom)); y++)
        {
            for (int x = image.Width / 100; x < image.Width / 2; x++)
            {
                double here = Luma(image, x, y);
                sum += Math.Abs(Luma(image, x + 1, y) - here) + Math.Abs(Luma(image, x, y + 1) - here);
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

    private static bool Within(double value, double reference, double share) => Math.Abs(value - reference) <= Math.Max(reference * share, 1e-4);

    public override void Render(float dt)
    {
        base.Render(dt);

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

/// <summary>Something in the world to hang the world's text on, a UI is a component and wants an entity.</summary>
internal sealed class WorldWords : GameObject
{ }
