using System.Diagnostics;
using System.Numerics;

namespace Horizon.Input;

public enum InputEventKind
{
    KeyDown, KeyUp, Text, PointerDown, PointerMove, PointerUp, PointerCancel, Scroll,
    GamepadConnected, GamepadDisconnected, GamepadState,
    /// <summary>A pinch of a trackpad, <see cref="InputEvent.Magnification"/> is the change of size.</summary>
    Magnify,
    /// <summary>A two finger double tap of a trackpad (smart zoom).</summary>
    SmartMagnify,
    /// <summary>A multi finger swipe of a trackpad, <see cref="InputEvent.SwipeX"/> is 1 for left and -1 for right.</summary>
    Swipe,
    /// <summary>An IME preedit composition update, <see cref="InputEvent.Text"/> is the marked text.</summary>
    Composition
}
public enum PointerKind { Mouse, Touch, Pen }

[Flags]
public enum KeyModifiers { None = 0, Shift = 1, Control = 2, Alt = 4, Super = 8 }

/// <summary>One platform event. Timestamps use <see cref="Stopwatch.GetTimestamp"/> units.</summary>
public readonly record struct InputEvent
{
    public InputEventKind Kind { get; init; }
    public long Timestamp { get; init; }
    public Key Key { get; init; }
    public KeyModifiers Modifiers { get; init; }
    /// <summary>True when the host supplied modifier state with the key event, including modifiers held before attachment.</summary>
    public bool HasModifierSnapshot { get; init; }
    public string? Text { get; init; }
    /// <summary>Clipboard text captured by the host for primary+V on its platform thread. Null when unavailable.</summary>
    public string? ClipboardText { get; init; }
    public PointerKind PointerKind { get; init; }
    public int PointerId { get; init; }
    public Vector2 Position { get; init; }
    public MouseButton Button { get; init; }
    public float ScrollY { get; init; }
    /// <summary>Horizontal wheel or trackpad delta, positive to the right (content follows the finger to the right, as ScrollY positive moves it up). Zero on a plain mouse.</summary>
    public float ScrollX { get; init; }
    /// <summary>
    /// True when ScrollX/ScrollY come from a continuous device (trackpad, momentum) as fractional "lines", rather than
    /// whole notches of a wheel. See <see cref="PreciseScroll"/> for the units.
    /// </summary>
    public bool ScrollPrecise { get; init; }
    /// <summary>
    /// Where a trackpad's gesture is, when the platform says (a Mac's <c>phase</c> and <c>momentumPhase</c>): the fingers on it,
    /// just lifted, or the glide after. Such an event can have no delta at all, it only says the fingers went.
    /// </summary>
    public ScrollPhase ScrollPhase { get; init; }
    /// <summary>For <see cref="InputEventKind.Magnify"/>: the change of size since the last event of the pinch, 0.02 for two per cent bigger.</summary>
    public float Magnification { get; init; }
    /// <summary>For <see cref="InputEventKind.Swipe"/>: 1 for a swipe to the left, -1 to the right.</summary>
    public int SwipeX { get; init; }
    /// <summary>For <see cref="InputEventKind.Composition"/>: cursor offset within <see cref="Text"/>.</summary>
    public int CompositionCursor { get; init; }
    /// <summary>For <see cref="InputEventKind.Composition"/>: selection length within <see cref="Text"/>.</summary>
    public int CompositionLength { get; init; }
    public GamepadDevice? Gamepad { get; init; }
    public GamepadSnapshot GamepadState { get; init; }

    public static InputEvent KeyChange(Key key, bool down) => new()
    {
        Kind = down ? InputEventKind.KeyDown : InputEventKind.KeyUp,
        Key = key, Timestamp = Stopwatch.GetTimestamp()
    };

    public static InputEvent Typed(string text) => new()
    {
        Kind = InputEventKind.Text, Text = text, Timestamp = Stopwatch.GetTimestamp()
    };

    public static InputEvent Composition(string text, int cursor, int length) => new()
    {
        Kind = InputEventKind.Composition, Text = text ?? string.Empty,
        CompositionCursor = cursor, CompositionLength = length,
        Timestamp = Stopwatch.GetTimestamp()
    };

    public static InputEvent Pointer(InputEventKind kind, Vector2 position, PointerKind pointerKind = PointerKind.Mouse,
        int pointerId = 0, MouseButton button = MouseButton.Left) => new()
    {
        Kind = kind, Position = position, PointerKind = pointerKind, PointerId = pointerId,
        Button = button, Timestamp = Stopwatch.GetTimestamp()
    };
}

/// <summary>A host publishes events in their original order, on its platform thread.</summary>
public interface IInputSource
{
    event Action<InputEvent>? EventReceived;
}
