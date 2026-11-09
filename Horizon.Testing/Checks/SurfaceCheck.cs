using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

using Horizon.Engine;
using Horizon.Graphics;


using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Testing.Checks;

/// <summary>
/// Checks the window's surface can be taken away and given back with the device and everything made on it staying,
/// which is what a phone does every time the game goes to the background. Three times over it draws, has the surface
/// taken away, draws on with nothing shown, resizes the window while there's no surface, gives the surface back and
/// draws again, checking every time that it is the same device and that a render target made at the start still has
/// in it what was put there. All of it on the render thread, in Render, where the GPU is.
/// </summary>
internal sealed class SurfaceCheck : Scene, ISelfCheck
{
    private static readonly Vector2 DesignSize = new(1600, 900);
    private static readonly Vector2[] Sizes = [new(1280, 720), new(1600, 900), new(1360, 768)];
    private const int CYCLES = 3;
    private const byte SENTINEL = 0x5A;

    public override Camera ActiveCamera { get; protected set; }

    private readonly UICompositor _compositor;
    private readonly TestChecks _checks = new("Surface");
    private readonly Queue<Func<bool>> _steps = new();

    private volatile bool _finished;
    private volatile int _count, _failed;

    public bool Finished => _finished;
    public int Failed => _failed;
    public int Count => _count;
    public int Skipped => _skipped;

    // A phone's surface is the size of its screen and nothing asks it to be another, so where the platform can't take a size
    // the checks of a resize are said to be skipped, and the rest of every cycle runs the same
    private const string NO_RESIZE = "the platform can't resize its window (no PlatformFeatures.DisplaySettings), its surface is the size of the screen";
    private bool _resizes;
    private volatile int _skipped;

    private Label _status = null!;
    private RenderTarget _probe = null!;
    private GraphicsDevice _device = null!;
    private nint _deviceHandle;
    private Vector2 _startSize;

    // What the step waiting on frames or a request is waiting for
    private int _waitFrames;
    private Task<bool>? _detaching;
    private Task<SurfaceAttachResult>? _attaching;
    private Task<SurfaceAttachResult>? _attachingAgain;
    private Task<bool>? _detachingAgain;

    // What the cycle under way saw before the surface went
    private int _generationBefore;
    private int _pipelinesBefore;
    private ulong _framesBefore;

    public SurfaceCheck()
    {
        var camera = AddEntity(new Camera2D(DesignSize));
        ActiveCamera = camera;
        _compositor = AddComponent(new UICompositor(camera, UICompositor.DEFAULT_SKIN_DIRECTORY, UICompositor.DEFAULT_SKIN_FILE) { DesignSize = DesignSize });
    }

    public override void Initialize()
    {
        base.Initialize();

        var module = _compositor.CreateModule();
        _status = module.AddComponent(new Label("surface") { Anchor = Origin.TopLeft, Position = new Vector2(24, -24), TextScale = 0.25f });

        // Render thread, so the GPU is there to make things on
        _device = Engine.Graphics;
        _deviceHandle = _device.Device.Handle;
        _probe = RenderTarget.Create(RenderTargetDescription.Color(4, 4));
        _startSize = Engine.WindowManager.ViewportSize;
        _resizes = (Engine.WindowManager.Features & Horizon.Platform.PlatformFeatures.DisplaySettings) != 0;
        Engine.Graphics.ClearColor = new Vector4(0.12f, 0.1f, 0.18f, 1.0f);

        Script();
    }

    private void Script()
    {
        Frames(10);
        for (int cycle = 0; cycle < CYCLES; cycle++)
        {
            int round = cycle;
            Vector2 resizeTo = _resizes ? Sizes[round] : _startSize;
            byte[] before = Colour(round, 0), during = Colour(round, 1);

            // Drawing into the window, with a probe to see what the device keeps
            Step(() =>
            {
                Check($"cycle {round}, there is a surface with a picture this frame", () => _device.SurfaceState == SurfaceState.Attached && _device.HasSurface && _device.HasWindowImage);
                Check($"cycle {round}, the window can be read back", () => WindowAlpha() == 255);
                Check($"cycle {round}, the swapchain is the size of the window", () => _device.SwapchainWidth == (uint)Engine.WindowManager.ViewportSize.X && _device.SwapchainHeight == (uint)Engine.WindowManager.ViewportSize.Y);
                Check($"cycle {round}, the probe holds what was put in it", () => FillAndRead(before) == Hex(before));
                _generationBefore = _device.SwapchainGeneration;
                _pipelinesBefore = _device.Statistics.Pipelines;
                _detaching = Engine.WindowManager.RequestSurfaceDetach();
                return true;
            });
            Step(() => _detaching!.IsCompleted);
            Step(() =>
            {
                Check($"cycle {round}, the surface was taken away", () => _detaching!.Result);
                _framesBefore = _device.FrameNumber;
                return true;
            });
            Frames(5);

            // No surface, the frames go on drawing into render targets and nothing is shown
            Step(() =>
            {
                Check($"cycle {round}, frames go on without a surface", () => _device.FrameNumber >= _framesBefore + 5);
                Check($"cycle {round}, nothing is taken off a swapchain that isn't there", () => _device.SurfaceState == SurfaceState.Detached && !_device.HasSurface && !_device.HasWindowImage);
                Check($"cycle {round}, reading the window back reads nothing", () => WindowAlpha() == SENTINEL);
                Check($"cycle {round}, it is the same device", () => ReferenceEquals(GraphicsDevice.Current, _device) && _device.Device.Handle == _deviceHandle);
                Check($"cycle {round}, the probe still holds what it had", () => Read() == Hex(before));
                Check($"cycle {round}, the probe can be drawn into with no surface", () => FillAndRead(during) == Hex(during));
                _detachingAgain = Engine.WindowManager.RequestSurfaceDetach();

                // Resized while there's no surface, the swapchain that comes back has to fit the new size
                if (_resizes) Engine.WindowManager.Apply(Engine.WindowManager.Display with { WindowSize = resizeTo });
                return true;
            });
            Step(() => _detachingAgain!.IsCompleted);
            Step(() =>
            {
                Check($"cycle {round}, taking away a surface that isn't there does nothing", () => !_detachingAgain!.Result);
                return true;
            });
            Frames(15);

            // The surface back, the same device carries on with it
            Step(() =>
            {
                _attaching = Engine.WindowManager.RequestSurfaceAttach();
                _attachingAgain = Engine.WindowManager.RequestSurfaceAttach();
                return true;
            });
            Step(() => _attaching!.IsCompleted && _attachingAgain!.IsCompleted);
            Step(() =>
            {
                Check($"cycle {round}, the surface was given back", () => _attaching!.Result == SurfaceAttachResult.Attached);
                Check($"cycle {round}, giving it twice says it is there already", () => _attachingAgain!.Result == SurfaceAttachResult.AlreadyAttached);
                return true;
            });
            Frames(10);
            Step(() =>
            {
                Check($"cycle {round}, frames are shown again", () => _device.SurfaceState == SurfaceState.Attached && _device.HasSurface && _device.HasWindowImage);
                Check($"cycle {round}, it is still the same device", () => ReferenceEquals(GraphicsDevice.Current, _device) && _device.Device.Handle == _deviceHandle);
                Check($"cycle {round}, the probe kept what it had through it all", () => Read() == Hex(during));
                Check($"cycle {round}, a new swapchain was made", () => _device.SwapchainGeneration > _generationBefore);
                CheckResize($"cycle {round}, the window took its new size", () => Engine.WindowManager.ViewportSize == resizeTo);
                Check($"cycle {round}, the new swapchain fits it", () => _device.SwapchainWidth == (uint)Engine.WindowManager.ViewportSize.X && _device.SwapchainHeight == (uint)Engine.WindowManager.ViewportSize.Y);
                Check($"cycle {round}, the window can be read back again", () => WindowAlpha() == 255);
                Check($"cycle {round}, every pipeline made before is used again", () => _device.Statistics.Pipelines == _pipelinesBefore);
                return true;
            });
        }

        // And back to the size it started at, resized with the surface there the way it always was
        Step(() =>
        {
            if (_resizes) Engine.WindowManager.Apply(Engine.WindowManager.Display with { WindowSize = _startSize });
            return true;
        });
        Frames(15);
        Step(() =>
        {
            CheckResize("a resize with the surface there makes the swapchain fit", () => _device.HasSurface && _device.SwapchainWidth == (uint)Engine.WindowManager.ViewportSize.X && _device.SwapchainHeight == (uint)Engine.WindowManager.ViewportSize.Y);
            Check("the probe is still the one made at the start", () => _probe.IsValid && Read() == Hex(Colour(CYCLES - 1, 1)));
            _checks.Report();
            _finished = true;
            return true;
        });
    }

    public override void Render(float dt)
    {
        base.Render(dt);

        // One step a frame at most, so whatever a step asked for gets a frame in between
        if (_steps.TryPeek(out Func<bool>? step) && step())
            _steps.Dequeue();
    }

    public override void UpdateState(float dt)
    {
        base.UpdateState(dt);
        _status.Text = $"surface  {_count} checked, {_failed} failed";
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

    // A check of a resize, made where the window takes a size and said to be skipped where it can't
    private void CheckResize(string name, Func<bool> check)
    {
        if (_resizes)
        {
            Check(name, check);
            return;
        }

        _checks.Skip(name, NO_RESIZE);
        _skipped++;
    }

    // A colour of its own for every cycle and stage, so a probe that came back with an old one is seen
    private static byte[] Colour(int cycle, int stage) => [(byte)(40 + cycle * 60), (byte)(stage == 0 ? 200 : 30), (byte)(90 + stage * 100), 255];

    private static string Hex(byte[] rgba) => Convert.ToHexString(rgba);

    /// <summary>Helper method to fill the probe with a colour and read it back.</summary>
    private string FillAndRead(byte[] rgba)
    {
        RenderTarget? bound = _device.BoundRenderTarget;
        _device.BindWindow();
        _device.ClearColorAttachment(_probe, 0, new Vector4(rgba[0], rgba[1], rgba[2], rgba[3]) / 255.0f);
        _device.BindRenderTarget(bound);
        return Read();
    }

    /// <summary>Helper method to read the middle of the probe.</summary>
    private string Read()
    {
        RenderTarget? bound = _device.BoundRenderTarget;
        Span<byte> pixel = stackalloc byte[4];
        _device.BindRenderTarget(_probe);
        _device.ReadPixels(1, 1, 1, 1, pixel);
        _device.BindRenderTarget(bound);
        return Convert.ToHexString(pixel);
    }

    /// <summary>Helper method to read the alpha of a pixel of the window, the sentinel if there's no picture to read.</summary>
    private int WindowAlpha()
    {
        RenderTarget? bound = _device.BoundRenderTarget;
        Span<byte> pixel = [SENTINEL, SENTINEL, SENTINEL, SENTINEL];
        _device.BindWindow();
        _device.ReadPixels(0, 0, 1, 1, pixel);
        _device.BindRenderTarget(bound);
        return pixel[3];
    }
}
