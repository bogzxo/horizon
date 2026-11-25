using System.Drawing;

using Horizon.Platform.Desktop;

namespace Horizon.Tests.Platform;

public sealed class WaylandTitleBarHitTestTests
{
    [Fact]
    public void VerticalMotionAcrossTitleBarAndTopBorder_NeverTriggersDoubleClick()
    {
        var tracker = new SdlBarClickTracker();
        long time = 1_000;
        var point = new Point(400, 12);
        long[] gaps = [50, 100, 250];

        // Simulate 100 vertical oscillations across the titlebar and top resize border at the exact same pixel.
        // Each pointer motion invokes HitTest (NoteBarHitTest) followed immediately by SDL_MOUSEMOTION (CancelPendingMotion).
        for (int i = 0; i < 100; i++)
        {
            time += gaps[i % gaps.Length];
            tracker.NoteBarHitTest(time, point);
            tracker.CancelPendingMotion();
            Assert.False(tracker.FlushPending());
        }
    }

    [Fact]
    public void ExitingWindowTopAndReenteringSamePixelAfter120Ms_NeverTriggersDoubleClick()
    {
        var tracker = new SdlBarClickTracker();
        var point = new Point(512, 8);

        // First motion near the top edge of the title bar
        tracker.NoteBarHitTest(2_000, point);
        tracker.CancelPendingMotion();
        Assert.False(tracker.FlushPending());

        // Pointer leaves top edge of window surface for 120 ms, then re-enters at the exact same pixel
        tracker.NoteBarHitTest(2_120, point);
        tracker.CancelPendingMotion();
        Assert.False(tracker.FlushPending());
    }

    [Fact]
    public void SingleClickFollowedBySmallMotionWithin150Ms_NeverTriggersDoubleClick()
    {
        var tracker = new SdlBarClickTracker();

        // First real click: HitTest with no accompanying SDL_MOUSEMOTION
        tracker.NoteBarHitTest(5_000, new Point(300, 14));
        Assert.False(tracker.FlushPending());

        // 150 ms later, pointer wiggles 2 px on the titlebar (HitTest + SDL_MOUSEMOTION)
        tracker.NoteBarHitTest(5_150, new Point(302, 14));
        tracker.CancelPendingMotion();
        Assert.False(tracker.FlushPending());
    }

    [Fact]
    public void ClickFollowedByWindowMoveOrResizeThenSecondClick_NeverTriggersDoubleClick()
    {
        var tracker = new SdlBarClickTracker();
        var point = new Point(300, 16);

        // First real click
        tracker.NoteBarHitTest(10_000, point);
        Assert.False(tracker.FlushPending());

        // Window moves or resizes (SDL_WINDOWEVENT_MOVED / RESIZED / SIZE_CHANGED)
        tracker.Reset();

        // Second real click within 200 ms at the exact same window-local point
        tracker.NoteBarHitTest(10_200, point);
        Assert.False(tracker.FlushPending());
    }

    [Fact]
    public void TwoGenuineClicksWithinWindowAndSlop_TriggersMaximizeOnceAndThirdClickStartsFresh()
    {
        var tracker = new SdlBarClickTracker();

        // First genuine click
        tracker.NoteBarHitTest(20_000, new Point(350, 15));
        Assert.False(tracker.FlushPending());

        // Small pointer motion between clicks (stays within 4 px slop)
        tracker.NoteBarHitTest(20_080, new Point(352, 16));
        tracker.CancelPendingMotion();
        Assert.False(tracker.FlushPending());

        // Second genuine click within 450 ms and <= 4 px
        tracker.NoteBarHitTest(20_220, new Point(353, 17));
        Assert.True(tracker.FlushPending());

        // Third click shortly after starts a fresh click sequence and must not double-trigger
        tracker.NoteBarHitTest(20_340, new Point(353, 17));
        Assert.False(tracker.FlushPending());
    }

    [Fact]
    public void TwoGenuineClicksBeyondDoubleClickTimeoutOrSlop_DoNotTriggerMaximize()
    {
        var tracker = new SdlBarClickTracker();

        // Exceeds timeout (>= 450 ms)
        tracker.NoteBarHitTest(30_000, new Point(200, 10));
        Assert.False(tracker.FlushPending());
        tracker.NoteBarHitTest(30_000 + SdlBarClickTracker.DoubleClickMs, new Point(200, 10));
        Assert.False(tracker.FlushPending());

        // Exceeds slop (> 4 px)
        tracker.Reset();
        tracker.NoteBarHitTest(40_000, new Point(200, 10));
        Assert.False(tracker.FlushPending());
        tracker.NoteBarHitTest(40_150, new Point(200 + SdlBarClickTracker.DoubleClickSlop + 1, 10));
        Assert.False(tracker.FlushPending());
    }
}
