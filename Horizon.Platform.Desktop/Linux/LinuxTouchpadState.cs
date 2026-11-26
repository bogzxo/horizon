using System.Diagnostics;

using Horizon.Input;

namespace Horizon.Platform.Desktop;

/// <summary>
/// Pure, platform-agnostic state machine for Linux touchpad scroll frames and multi-finger gestures
/// (Wayland <c>wl_pointer</c> + <c>zwp_pointer_gestures_v1</c>, and X11 XInput 2.4 gestures).
/// Kept separate from native FFI so every transition is unit-tested on all platforms.
/// </summary>
internal sealed class LinuxTouchpadState
{
    // Wayland wl_pointer_axis constants
    public const uint AxisVertical = 0;
    public const uint AxisHorizontal = 1;

    // Wayland wl_pointer_axis_source constants
    public const uint SourceWheel = 0;
    public const uint SourceFinger = 1;
    public const uint SourceContinuous = 2;
    public const uint SourceWheelTilt = 3;

    // Minimum horizontal travel (surface points) and horizontal dominance ratio for a 3+ finger swipe
    internal const double SwipeThreshold = 40.0;
    internal const double SwipeDominance = 1.5;

    // Per-frame Wayland wl_pointer accumulators (cleared on each wl_pointer.frame)
    private bool frameHasSource;
    private uint frameSource;
    private double frameX, frameY;
    private bool frameStopped;

    // Live gesture state
    private bool fingersTouching;
    private bool holdActive;
    private bool pinchActive;
    private double lastPinchScale = 1.0;
    private bool swipeActive;
    private uint swipeFingers;
    private double swipeDx, swipeDy;

    /// <summary>Whether fingers are currently tracked as touching the touchpad (scroll or hold).</summary>
    public bool FingersTouching => fingersTouching;

    /// <summary>Records the Wayland <c>wl_pointer.axis_source</c> for the current pointer frame.</summary>
    public void OnAxisSource(uint source)
    {
        frameHasSource = true;
        frameSource = source;
    }

    /// <summary>
    /// Accumulates a Wayland <c>wl_pointer.axis</c> delta in surface-local logical pixels.
    /// Positive Wayland values mean scroll down (vertical) or scroll right (horizontal);
    /// Horizon's <see cref="InputEvent.ScrollY"/> and <see cref="InputEvent.ScrollX"/> are negated
    /// so content follows the fingers 1:1 (natural scrolling is already applied by libinput).
    /// </summary>
    public void OnAxis(uint axis, double surfacePixels)
    {
        if (!double.IsFinite(surfacePixels))
            return;

        if (axis == AxisVertical)
            frameY -= surfacePixels;
        else if (axis == AxisHorizontal)
            frameX -= surfacePixels;
    }

    /// <summary>Records <c>wl_pointer.axis_stop</c> (fingers lifted off the touchpad).</summary>
    public void OnAxisStop() => frameStopped = true;

    /// <summary>
    /// Commits a Wayland <c>wl_pointer.frame</c>. Returns the <see cref="InputEvent"/> to raise (if any)
    /// and whether SDL's duplicate <c>SDL_MOUSEWHEEL</c> for this frame should be suppressed.
    /// </summary>
    public (InputEvent? Event, bool SuppressSdlWheel) OnFrame(bool onOurSurface, long timestamp = 0)
    {
        bool hasSource = frameHasSource;
        uint source = frameSource;
        double dx = frameX;
        double dy = frameY;
        bool stopped = frameStopped;

        frameHasSource = false;
        frameSource = 0;
        frameX = 0.0;
        frameY = 0.0;
        frameStopped = false;

        if (!onOurSurface)
        {
            if (fingersTouching)
            {
                fingersTouching = false;
                return (MakeScroll(0.0f, 0.0f, ScrollPhase.Released, timestamp), false);
            }
            return (null, false);
        }

        bool hasDelta = dx != 0.0 || dy != 0.0;
        bool isTouchpad = hasSource
            ? source is SourceFinger or SourceContinuous
            : (stopped || hasDelta) && fingersTouching;

        if (!isTouchpad)
            return (null, false);

        // Wayland axis values are in surface-local logical points; divide by NotchPoints (48) so
        // ScrollPanel / CodeEditor move by the exact finger distance (1:1 pixels).
        float notchesX = (float)(dx / PreciseScroll.NotchPoints);
        float notchesY = (float)(dy / PreciseScroll.NotchPoints);

        if (stopped)
        {
            fingersTouching = false;
            return (MakeScroll(notchesX, notchesY, ScrollPhase.Released, timestamp), hasDelta);
        }

        if (hasDelta)
        {
            fingersTouching = true;
            return (MakeScroll(notchesX, notchesY, ScrollPhase.Touching, timestamp), true);
        }

        return (null, false);
    }

    /// <summary>Handles pointer leave or window focus loss: releases any held fingers so overscroll springs back.</summary>
    public InputEvent? OnPointerLeave(long timestamp = 0)
    {
        frameHasSource = false;
        frameX = frameY = 0.0;
        frameStopped = false;
        holdActive = false;
        pinchActive = false;
        lastPinchScale = 1.0;
        swipeActive = false;

        if (!fingersTouching)
            return null;

        fingersTouching = false;
        return MakeScroll(0.0f, 0.0f, ScrollPhase.Released, timestamp);
    }

    /// <summary>
    /// Handles a Wayland <c>zwp_pointer_gesture_hold_v1.begin</c> (2+ fingers resting on touchpad).
    /// Emits a zero-delta <see cref="ScrollPhase.Touching"/> event so active kinetic momentum stops immediately.
    /// </summary>
    public InputEvent? OnHoldBegin(uint fingers, long timestamp = 0)
    {
        if (fingers < 2)
            return null;

        holdActive = true;
        fingersTouching = true;
        return MakeScroll(0.0f, 0.0f, ScrollPhase.Touching, timestamp);
    }

    /// <summary>
    /// Handles <c>zwp_pointer_gesture_hold_v1.end</c>. If not cancelled (user lifted fingers without
    /// transitioning into a scroll or pinch), releases <see cref="ScrollPhase.Touching"/>.
    /// </summary>
    public InputEvent? OnHoldEnd(bool cancelled, long timestamp = 0)
    {
        if (!holdActive)
            return null;

        holdActive = false;
        if (cancelled || !fingersTouching)
            return null;

        fingersTouching = false;
        return MakeScroll(0.0f, 0.0f, ScrollPhase.Released, timestamp);
    }

    /// <summary>Begins a two-finger pinch gesture (Wayland or XInput 2.4). Releases any active hold.</summary>
    public InputEvent? OnPinchBegin(uint fingers, double initialScale = 1.0, long timestamp = 0)
    {
        holdActive = false;
        pinchActive = fingers >= 2;
        lastPinchScale = initialScale > 0.0 && double.IsFinite(initialScale) ? initialScale : 1.0;

        if (!fingersTouching)
            return null;

        fingersTouching = false;
        return MakeScroll(0.0f, 0.0f, ScrollPhase.Released, timestamp);
    }

    /// <summary>
    /// Updates an active pinch with the cumulative absolute <paramref name="scale"/> relative to pinch begin
    /// (e.g. 1.05 means fingers are 5% further apart than at begin). Emits incremental <see cref="InputEventKind.Magnify"/>.
    /// </summary>
    public InputEvent? OnPinchUpdate(double scale, long timestamp = 0)
    {
        if (!pinchActive || scale <= 0.0 || !double.IsFinite(scale))
            return null;

        double delta = (scale / lastPinchScale) - 1.0;
        lastPinchScale = scale;
        if (Math.Abs(delta) < 1e-6)
            return null;

        return new InputEvent
        {
            Kind = InputEventKind.Magnify,
            Magnification = (float)delta,
            Timestamp = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp
        };
    }

    /// <summary>Ends a pinch gesture.</summary>
    public void OnPinchEnd()
    {
        pinchActive = false;
        lastPinchScale = 1.0;
    }

    /// <summary>Begins a multi-finger swipe gesture (Wayland or XInput 2.4). Releases any active hold.</summary>
    public InputEvent? OnSwipeBegin(uint fingers, long timestamp = 0)
    {
        holdActive = false;
        swipeActive = fingers >= 3;
        swipeFingers = fingers;
        swipeDx = 0.0;
        swipeDy = 0.0;

        if (!fingersTouching)
            return null;

        fingersTouching = false;
        return MakeScroll(0.0f, 0.0f, ScrollPhase.Released, timestamp);
    }

    /// <summary>Accumulates relative surface-pixel movement during a multi-finger swipe.</summary>
    public void OnSwipeUpdate(double dx, double dy)
    {
        if (!swipeActive)
            return;
        if (double.IsFinite(dx))
            swipeDx += dx;
        if (double.IsFinite(dy))
            swipeDy += dy;
    }

    /// <summary>
    /// Ends a multi-finger swipe gesture. Emits <see cref="InputEventKind.Swipe"/> (<c>SwipeX = 1</c> for left,
    /// <c>-1</c> for right) when an uncancelled 3+ finger swipe crosses the horizontal threshold.
    /// </summary>
    public InputEvent? OnSwipeEnd(bool cancelled, long timestamp = 0)
    {
        bool wasActive = swipeActive;
        uint fingers = swipeFingers;
        double dx = swipeDx;
        double dy = swipeDy;
        swipeActive = false;
        swipeFingers = 0;
        swipeDx = 0.0;
        swipeDy = 0.0;

        if (!wasActive || cancelled || fingers < 3)
            return null;

        if (Math.Abs(dx) < SwipeThreshold || Math.Abs(dx) <= Math.Abs(dy) * SwipeDominance)
            return null;

        return new InputEvent
        {
            Kind = InputEventKind.Swipe,
            SwipeX = dx < 0.0 ? 1 : -1,
            Timestamp = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp
        };
    }

    private static InputEvent MakeScroll(float x, float y, ScrollPhase phase, long timestamp) => new()
    {
        Kind = InputEventKind.Scroll,
        ScrollX = x,
        ScrollY = y,
        ScrollPrecise = true,
        ScrollPhase = phase,
        Timestamp = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp
    };
}
