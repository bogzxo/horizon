using System.Diagnostics;
using System.Numerics;

namespace Horizon.Input;

public enum InputEventKind
{
    KeyDown, KeyUp, Text, PointerDown, PointerMove, PointerUp, PointerCancel, Scroll,
    GamepadConnected, GamepadDisconnected, GamepadState
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
