namespace Horizon.Input;

/// <summary>Where a trackpad's scroll gesture is, for the rubber band at the ends: held by the fingers, let go, or gliding.</summary>
public enum ScrollPhase : byte
{
    /// <summary>Not said, a wheel or a platform that doesn't tell.</summary>
    None,

    /// <summary>The fingers are on the trackpad, what's past an end is held there with the give of a rubber band.</summary>
    Touching,

    /// <summary>The fingers came off, what was held past an end springs back (the glide may follow).</summary>
    Released,

    /// <summary>The glide after a flick, the system's momentum, which bounces off an end.</summary>
    Momentum,

    /// <summary>The glide is over.</summary>
    MomentumEnded,
}

/// <summary>
/// The arithmetic of trackpad (precise) scrolling, apart from any window. Units:
/// <list type="bullet">
/// <item>A wheel notch is 1.0 in <see cref="InputEvent.ScrollY"/> and is worth <see cref="NotchPoints"/> layout units in the UI.</item>
/// <item>On macOS a precise device's turn is <c>NSEvent.scrollingDeltaX/Y</c> (points, already in natural direction, momentum
/// included as more events), read from the event by <c>MacScrollWatch</c> and scaled by 0.1, so one unit is
/// <see cref="MacPointsPerUnit"/> points. SDL2's own figure is AppKit's <c>deltaY</c> in lines, and Silk only hands on the
/// whole notches SDL adds it up to. The UI works in layout units that are the same points, so the finger moves content 1:1.</item>
/// <item>SDL2 elsewhere (Linux libinput smooth scrolling) gives fractions of a notch, so one unit is a whole notch.</item>
/// </list>
/// SDL has no flag for "from a precise device", so on Linux <see cref="LooksPrecise"/> takes a fractional delta as the sign of one;
/// on a Mac the flag is read from the <c>NSEvent</c> itself (<c>MacScrollWatch</c>).
/// </summary>
public static class PreciseScroll
{
    /// <summary>
    /// The phase of a trackpad's scroll as AppKit's <c>NSEvent</c> says it, <see cref="ScrollPhase.None"/> where the platform
    /// doesn't. <paramref name="phase"/> and <paramref name="momentum"/> are <c>NSEventPhase</c> flags (began 1, stationary 2,
    /// changed 4, ended 8, cancelled 16, may begin 32).
    /// </summary>
    public static ScrollPhase PhaseOf(long phase, long momentum)
    {
        if ((momentum & (1 | 4 | 2)) != 0) return ScrollPhase.Momentum;
        if ((momentum & (8 | 16)) != 0) return ScrollPhase.MomentumEnded;
        if ((phase & (1 | 2 | 4 | 32)) != 0) return ScrollPhase.Touching;
        if ((phase & (8 | 16)) != 0) return ScrollPhase.Released;
        return ScrollPhase.None;
    }

    /// <summary>Layout units one notch of a wheel scrolls, the same as the finger distance for a notch of a pan.</summary>
    public const float NotchPoints = 48.0f;

    /// <summary>Points one SDL wheel unit is on a Mac for a precise device, undoing SDL's 0.1.</summary>
    public const float MacPointsPerUnit = 10.0f;

    /// <summary>How near a whole number a delta can be and still count as one.</summary>
    private const float WHOLE = 1e-3f;

    /// <summary>Whether a wheel delta from SDL is a fractional one, which no notched wheel makes.</summary>
    public static bool LooksPrecise(float x, float y) => Fractional(x) || Fractional(y);

    private static bool Fractional(float value) => MathF.Abs(value - MathF.Round(value)) > WHOLE;

    /// <summary>How many layout units a precise delta of the platform's SDL is worth.</summary>
    public static float ToPoints(float delta, bool mac) => delta * (mac ? MacPointsPerUnit : NotchPoints);

    /// <summary>The delta as notches, what <c>OnScroll</c> takes, fractional for a precise one.</summary>
    public static float ToNotches(float delta, bool mac) => ToPoints(delta, mac) / NotchPoints;

    /// <summary>The delta as notches for the platform this runs on.</summary>
    public static float ToNotches(float delta) => ToNotches(delta, OperatingSystem.IsMacOS());

    /// <summary>
    /// The delta as notches when a unit of the layout isn't a point of the screen: <paramref name="unitsPerPoint"/> units of the
    /// layout are a point (1 when the interface is at 100%, 0.8 at 125%), so a finger's point moves the content by a point
    /// whatever the interface size. Only a Mac's trackpad counts in points, anywhere else the notch is what it was.
    /// </summary>
    public static float ToNotches(float delta, bool mac, float unitsPerPoint) =>
        ToNotches(delta, mac) * (mac && unitsPerPoint > 0.0f && float.IsFinite(unitsPerPoint) ? unitsPerPoint : 1.0f);

    /// <summary>The delta as notches for the platform this runs on, with the layout units a point of the screen is.</summary>
    public static float ToNotchesAt(float delta, float unitsPerPoint) => ToNotches(delta, OperatingSystem.IsMacOS(), unitsPerPoint);

    /// <summary>
    /// What a turn that zooms (Ctrl and the wheel) is worth in steps of a zoom: a notched wheel's turn is one step either way
    /// whatever its size, a precise one's is the fraction of a step it was, so a trackpad zooms smoothly and not a step for
    /// each of its dozens of events.
    /// </summary>
    public static float ZoomSteps(float notches, bool precise) => precise ? notches : MathF.Sign(notches);

    /// <summary>The factor of a number of steps of a zoom (<paramref name="step"/> is the factor of one): 1 step is <paramref name="step"/>, -1 is its inverse.</summary>
    public static float ZoomFactor(float steps, float step) => MathF.Pow(step, steps);

    /// <summary>The largest change of size one pinch event is let make, a bad value from the system doesn't send the zoom to its end.</summary>
    public const float MaxPinch = 0.5f;

    /// <summary>The factor of a pinch event, <c>NSEvent.magnification</c> being the change of size (0.02 for two per cent bigger).</summary>
    public static float PinchFactor(float magnification) => 1.0f + Math.Clamp(magnification, -MaxPinch, MaxPinch);

    /// <summary>What a smart zoom (two finger double tap) goes to: <paramref name="larger"/> from the usual size, the usual size from anywhere else.</summary>
    public static float SmartZoomTarget(float current, float usual, float larger) =>
        MathF.Abs(current - usual) < 0.02f * usual ? larger : usual;
}
