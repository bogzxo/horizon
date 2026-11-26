using Horizon.Input;
using Horizon.Platform.Desktop;

namespace Horizon.Tests.Input;

/// <summary>Linux Wayland / XInput 2.4 touchpad scroll frames, phase transitions, hold, pinch, and swipe gestures.</summary>
public class LinuxTouchpadTests
{
    [Fact]
    public void Finger_scroll_tracks_surface_pixels_one_to_one_and_suppresses_sdl_wheel()
    {
        var pad = new LinuxTouchpadState();
        pad.OnAxisSource(LinuxTouchpadState.SourceFinger);
        pad.OnAxis(LinuxTouchpadState.AxisVertical, 24.0);   // down 24px
        pad.OnAxis(LinuxTouchpadState.AxisHorizontal, 12.0); // right 12px

        var (ev, suppress) = pad.OnFrame(onOurSurface: true, timestamp: 100);
        Assert.True(suppress);
        Assert.True(pad.FingersTouching);
        Assert.NotNull(ev);
        Assert.Equal(InputEventKind.Scroll, ev!.Value.Kind);
        Assert.True(ev.Value.ScrollPrecise);
        Assert.Equal(ScrollPhase.Touching, ev.Value.ScrollPhase);
        // Negative ScrollY scrolls down (offset - delta * 48 = +24); negative ScrollX scrolls right (+12)
        Assert.Equal(-24.0f / PreciseScroll.NotchPoints, ev.Value.ScrollY, 5);
        Assert.Equal(-12.0f / PreciseScroll.NotchPoints, ev.Value.ScrollX, 5);
    }

    [Fact]
    public void Axis_stop_releases_fingers_for_glide_and_rubber_band_springback()
    {
        var pad = new LinuxTouchpadState();
        pad.OnAxisSource(LinuxTouchpadState.SourceFinger);
        pad.OnAxis(LinuxTouchpadState.AxisVertical, 18.0);
        var (moveEv, _) = pad.OnFrame(onOurSurface: true, timestamp: 1);
        Assert.Equal(ScrollPhase.Touching, moveEv!.Value.ScrollPhase);

        pad.OnAxisSource(LinuxTouchpadState.SourceFinger);
        pad.OnAxisStop();
        var (stopEv, suppress) = pad.OnFrame(onOurSurface: true, timestamp: 2);
        Assert.False(suppress);
        Assert.False(pad.FingersTouching);
        Assert.NotNull(stopEv);
        Assert.Equal(ScrollPhase.Released, stopEv!.Value.ScrollPhase);
        Assert.Equal(0.0f, stopEv.Value.ScrollY);
        Assert.Equal(0.0f, stopEv.Value.ScrollX);
    }

    [Fact]
    public void Physical_mouse_wheel_is_left_to_sdl_without_suppression()
    {
        var pad = new LinuxTouchpadState();
        pad.OnAxisSource(LinuxTouchpadState.SourceWheel);
        pad.OnAxis(LinuxTouchpadState.AxisVertical, 15.0);

        var (ev, suppress) = pad.OnFrame(onOurSurface: true, timestamp: 10);
        Assert.Null(ev);
        Assert.False(suppress);
        Assert.False(pad.FingersTouching);
    }

    [Fact]
    public void Hold_gesture_touches_immediately_and_releases_when_uncancelled()
    {
        var pad = new LinuxTouchpadState();
        var begin = pad.OnHoldBegin(fingers: 2, timestamp: 1);
        Assert.NotNull(begin);
        Assert.Equal(ScrollPhase.Touching, begin!.Value.ScrollPhase);
        Assert.True(pad.FingersTouching);

        // Uncancelled end (fingers lifted without moving) releases hold
        var end = pad.OnHoldEnd(cancelled: false, timestamp: 2);
        Assert.NotNull(end);
        Assert.Equal(ScrollPhase.Released, end!.Value.ScrollPhase);
        Assert.False(pad.FingersTouching);
    }

    [Fact]
    public void Hold_cancelled_into_pinch_releases_touching_on_pinch_begin()
    {
        var pad = new LinuxTouchpadState();
        Assert.NotNull(pad.OnHoldBegin(fingers: 2, timestamp: 1));
        Assert.Null(pad.OnHoldEnd(cancelled: true, timestamp: 2));
        Assert.True(pad.FingersTouching);

        var release = pad.OnPinchBegin(fingers: 2, initialScale: 1.0, timestamp: 3);
        Assert.NotNull(release);
        Assert.Equal(ScrollPhase.Released, release!.Value.ScrollPhase);
        Assert.False(pad.FingersTouching);
    }

    [Fact]
    public void Pinch_converts_cumulative_scale_to_incremental_magnification()
    {
        var pad = new LinuxTouchpadState();
        pad.OnPinchBegin(fingers: 2, initialScale: 1.0, timestamp: 1);

        var step1 = pad.OnPinchUpdate(1.10, timestamp: 2);
        Assert.NotNull(step1);
        Assert.Equal(InputEventKind.Magnify, step1!.Value.Kind);
        Assert.Equal(0.10f, step1.Value.Magnification, 5);

        // From 1.10 to 1.21 is another +10%
        var step2 = pad.OnPinchUpdate(1.21, timestamp: 3);
        Assert.NotNull(step2);
        Assert.Equal(0.10f, step2!.Value.Magnification, 5);

        // From 1.21 back to 1.089 is -10%
        var step3 = pad.OnPinchUpdate(1.089, timestamp: 4);
        Assert.NotNull(step3);
        Assert.Equal(-0.10f, step3!.Value.Magnification, 5);

        pad.OnPinchEnd();
        Assert.Null(pad.OnPinchUpdate(1.5, timestamp: 5));
    }

    [Fact]
    public void Three_finger_horizontal_swipe_emits_direction_and_ignores_vertical_or_cancelled()
    {
        var pad = new LinuxTouchpadState();

        // Swipe left -> SwipeX = +1 (next file)
        pad.OnSwipeBegin(fingers: 3, timestamp: 1);
        pad.OnSwipeUpdate(-35.0, 2.0);
        pad.OnSwipeUpdate(-30.0, -3.0);
        var left = pad.OnSwipeEnd(cancelled: false, timestamp: 2);
        Assert.NotNull(left);
        Assert.Equal(InputEventKind.Swipe, left!.Value.Kind);
        Assert.Equal(1, left.Value.SwipeX);

        // Swipe right -> SwipeX = -1 (previous file)
        pad.OnSwipeBegin(fingers: 3, timestamp: 3);
        pad.OnSwipeUpdate(60.0, 5.0);
        var right = pad.OnSwipeEnd(cancelled: false, timestamp: 4);
        Assert.NotNull(right);
        Assert.Equal(-1, right!.Value.SwipeX);

        // Vertical swipe ignored
        pad.OnSwipeBegin(fingers: 3, timestamp: 5);
        pad.OnSwipeUpdate(10.0, -90.0);
        Assert.Null(pad.OnSwipeEnd(cancelled: false, timestamp: 6));

        // Cancelled swipe ignored
        pad.OnSwipeBegin(fingers: 3, timestamp: 7);
        pad.OnSwipeUpdate(-80.0, 0.0);
        Assert.Null(pad.OnSwipeEnd(cancelled: true, timestamp: 8));
    }

    [Fact]
    public void Pointer_leave_releases_active_scroll_hold()
    {
        var pad = new LinuxTouchpadState();
        pad.OnAxisSource(LinuxTouchpadState.SourceFinger);
        pad.OnAxis(LinuxTouchpadState.AxisVertical, 10.0);
        pad.OnFrame(onOurSurface: true, timestamp: 1);
        Assert.True(pad.FingersTouching);

        var left = pad.OnPointerLeave(timestamp: 2);
        Assert.NotNull(left);
        Assert.Equal(ScrollPhase.Released, left!.Value.ScrollPhase);
        Assert.False(pad.FingersTouching);
    }
}
