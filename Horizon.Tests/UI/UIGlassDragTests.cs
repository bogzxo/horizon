using System.Diagnostics;
using System.Numerics;

using Horizon.Core;
using Horizon.Core.Threading;
using Horizon.Engine;
using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// Verifies zero-lag floating window drag behavior: uncapped frame-pacing deadline windows,
/// full-viewport glass capture region caching during pointer-following drags, and bypassing
/// <see cref="UICompositor.FrameRateLimit"/> for live pointer-following uploads.
/// </summary>
public class UIGlassDragTests
{
    private static UISkin CreateSkin() =>
        new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));

    [Fact]
    public void UncappedFramesFor_is_active_until_deadline_and_expires_after()
    {
        long start = (long)(10.0 * Stopwatch.Frequency);
        long uncappedUntil = 0;

        Assert.False(WindowManager.UncappedFramesFor(start, uncappedUntil));

        WindowManager.ExtendDeadline(ref uncappedUntil, start, 0.05);

        Assert.True(WindowManager.UncappedFramesFor(start, uncappedUntil));
        Assert.True(WindowManager.UncappedFramesFor(uncappedUntil - 1, uncappedUntil));
        Assert.False(WindowManager.UncappedFramesFor(uncappedUntil, uncappedUntil));
        Assert.False(WindowManager.UncappedFramesFor(uncappedUntil + 1, uncappedUntil));

        // Extending again before expiry moves the deadline forward monotonically
        long mid = start + (long)(0.03 * Stopwatch.Frequency);
        long previous = uncappedUntil;
        WindowManager.ExtendDeadline(ref uncappedUntil, mid, 0.05);
        Assert.True(uncappedUntil > previous);
        Assert.True(WindowManager.UncappedFramesFor(previous, uncappedUntil));
        Assert.False(WindowManager.UncappedFramesFor(uncappedUntil, uncappedUntil));
    }

    [Fact]
    public void GlassCaptureRegion_quantizes_to_128px_normally_and_uses_full_viewport_during_drag()
    {
        const int maxW = 1920;
        const int maxH = 1080;
        const int margin = 24;

        // Normal (non-drag) mode quantizes padded visible bounds to 128 px tiles
        var first = UICompositor.GlassCaptureRegion(new Vector4(140, 150, 420, 380), margin, maxW, maxH, fullViewport: false);
        Assert.Equal((0, 0, 512, 512), first);

        // Crossing a 128 px grid boundary changes the quantized crop when not dragging
        var shifted = UICompositor.GlassCaptureRegion(new Vector4(300, 290, 620, 540), margin, maxW, maxH, fullViewport: false);
        Assert.NotEqual(first, shifted);
        Assert.Equal((256, 256, 512, 384), shifted);

        // During a pointer-following drag over a stationary backdrop, region stays full viewport everywhere
        Vector4[] dragPositions =
        [
            new(10, 10, 260, 180),
            new(140, 150, 420, 380),
            new(640, 420, 960, 740),
            new(1500, 720, 1910, 1070)
        ];

        foreach (Vector4 pVis in dragPositions)
        {
            var region = UICompositor.GlassCaptureRegion(pVis, margin, maxW, maxH, fullViewport: true);
            Assert.Equal((0, 0, maxW, maxH), region);
        }
    }

    [Fact]
    public void FollowsPointer_window_drag_bypasses_FrameRateLimit_on_consecutive_render_frames()
    {
        const float dt = 1.0f / 120.0f;
        var skin = CreateSkin();
        var input = new InputManager();
        var camera = new Camera2D(new Vector2(800, 600));
        var screen = new UIRect(Vector2.Zero, new Vector2(800, 600));
        var clock = new SnapshotClock();

        using var compositor = new UICompositor(camera)
        {
            InputOverride = input,
            ScreenToWorldOverride = static p => p,
            FrameRateLimit = 10.0f // 100 ms interval — far slower than consecutive render frames
        };

        UIModule module = compositor.CreateModule();
        var window = new WindowPanel("Settings")
        {
            Position = new Vector2(100.0f, 100.0f),
            Size = new Vector2(220.0f, 160.0f),
            Closable = false,
            Glass = UIGlass.Frosted
        };
        module.Root.Add(window);

        // Initial layout tick so TitleBounds is populated
        input.UpdateState(dt);
        compositor.RouteForTest(screen, skin, dt);

        // Press on the title bar to start a FollowsPointer move drag
        Vector2 startPoint = new(window.TitleBounds.Min.X + 40.0f, window.TitleBounds.Center.Y);
        input.Mouse.Place(startPoint);
        input.Mouse.HearButton(MouseButton.Left, true);
        input.UpdateState(dt);
        compositor.RouteForTest(screen, skin, dt);

        // Publish captured snapshot with FollowsPointer frame active
        RenderFrame CaptureFrame(double simTime)
        {
            clock.BeginCapture();
            int slot = clock.CaptureSlot;
            long seq = clock.CaptureSequence;
            compositor.CaptureForTest(screen, skin, dt, clock);
            clock.EndCapture(Stopwatch.GetTimestamp(), simTime);
            return new RenderFrame(clock, slot, slot, seq, seq, 1.0f, simTime, 0.0f, 0.001f, PresentationMode.Latest);
        }

        RenderFrame dragFrame = CaptureFrame(dt);

        // First render frame is due (refreshedAt == 0) and uploads the initial drag state
        Assert.True(compositor.UploadCapturedForTest(dragFrame, skin));
        Assert.True(compositor.FollowingPointerForTest);
        long uploadsAfterFirst = compositor.Statistics.FramesUploaded;
        Assert.Equal(1, uploadsAfterFirst);

        // Consecutive render frames with live mouse movement well within the 100 ms FrameRateLimit still upload every frame
        input.Mouse.Place(startPoint + new Vector2(18.0f, 6.0f));
        Assert.True(compositor.UploadCapturedForTest(dragFrame, skin));
        Assert.Equal(uploadsAfterFirst + 1, compositor.Statistics.FramesUploaded);

        input.Mouse.Place(startPoint + new Vector2(42.0f, -12.0f));
        Assert.True(compositor.UploadCapturedForTest(dragFrame, skin));
        Assert.Equal(uploadsAfterFirst + 2, compositor.Statistics.FramesUploaded);

        // When the live pointer has not moved, no redundant upload occurs
        Assert.True(compositor.UploadCapturedForTest(dragFrame, skin));
        Assert.Equal(uploadsAfterFirst + 2, compositor.Statistics.FramesUploaded);

        // Releasing the mouse clears FollowingPointer and restores FrameRateLimit throttling for non-drag updates
        input.Mouse.HearButton(MouseButton.Left, false);
        input.UpdateState(dt);
        compositor.RouteForTest(screen, skin, dt);
        window.Position += new Vector2(10.0f, 0.0f);
        RenderFrame releasedFrame = CaptureFrame(dt * 2.0);

        long uploadsBeforeReleased = compositor.Statistics.FramesUploaded;
        Assert.True(compositor.UploadCapturedForTest(releasedFrame, skin));
        Assert.False(compositor.FollowingPointerForTest);
        Assert.Equal(uploadsBeforeReleased, compositor.Statistics.FramesUploaded);
    }
}
