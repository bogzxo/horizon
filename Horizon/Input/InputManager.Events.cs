using System.Collections.Concurrent;

namespace Horizon.Input;

public sealed partial class InputManager
{
    private readonly ConcurrentQueue<InputEvent> _events = new();

    // HORIZON_INPUT_LOG=keys (or on) writes every key down and up to the log, the way HORIZON_INPUT_LOG=gamepads does
    // the pads, capped the same
    private static readonly bool LogsKeys = System.Environment.GetEnvironmentVariable("HORIZON_INPUT_LOG") is { } wanted
        && (wanted.Equals("keys", StringComparison.OrdinalIgnoreCase) || wanted.Equals("on", StringComparison.OrdinalIgnoreCase) || wanted == "1");
    private int _keysLogged;
    private readonly bool[] _eventKeys = new bool[512];
    private IInputSource? _source;

    /// <summary>
    /// What pointed at the screen last, the mouse or a finger (or a pen). Moves on as the events of a tick are taken
    /// in, so a UI can stop showing the mouse's hover the moment a finger is used, and show it again once the mouse moves.
    /// </summary>
    public PointerKind LastPointerKind { get; private set; }

    /// <summary>Ordered key, text and pointer events delivered on the simulation thread before game updates.</summary>
    public event Action<InputEvent>? EventReceived;

    /// <summary>Installs the host's input source. A manager has one source for its lifetime.</summary>
    public void AttachSource(IInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_source is not null) throw new InvalidOperationException("This input manager already has a source.");
        _source = source;
        source.EventReceived += Post;
    }

    /// <summary>Queues a platform or scripted event from any thread. Live mouse state changes immediately.</summary>
    public void Post(InputEvent input)
    {
        if (input.PointerKind == PointerKind.Mouse)
        {
            if (input.Kind is InputEventKind.PointerDown or InputEventKind.PointerMove or InputEventKind.PointerUp)
                Mouse.Place(input.Position);
            if (input.Kind is InputEventKind.PointerDown or InputEventKind.PointerUp)
                Mouse.SetLiveButton(input.Button, input.Kind == InputEventKind.PointerDown);
            if (input.Kind == InputEventKind.PointerCancel) Mouse.CancelLiveButtons();
        }
        _events.Enqueue(input);
    }

    /// <summary>Whether the soft keyboard was last asked to be up, see <see cref="SetTextInputActive"/>. For a test.</summary>
    public bool TextInputActive { get; private set; }

    /// <summary>
    /// Asks the platform for its soft keyboard while something takes text, or to put it away. Horizon.UI does this for
    /// a text box a finger focused. From any thread.
    /// </summary>
    public void SetTextInputActive(bool active)
    {
        TextInputActive = active;
        Platform?.SetTextInputActive(active);
    }

    private void DrainEvents()
    {
        Touch.BeginTick();
        while (_events.TryDequeue(out var input))
        {
            switch (input.Kind)
            {
                case InputEventKind.KeyDown:
                case InputEventKind.KeyUp:
                    if ((uint)input.Key >= _eventKeys.Length) continue;
                    bool down = input.Kind == InputEventKind.KeyDown;
                    _eventKeys[(int)input.Key] = down;
                    if (!input.HasModifierSnapshot) input = input with { Modifiers = CurrentModifiers() };
                    if (LogsKeys && _keysLogged < 600 && ++_keysLogged > 0) Horizon.Logging.Log.Info($"[Input] key {input.Key} {(down ? "down" : "up")}");
                    Keyboard.Accept(input.Key, down);
                    break;
                case InputEventKind.PointerDown:
                case InputEventKind.PointerUp:
                    if (input.PointerKind == PointerKind.Mouse)
                        Mouse.HearButton(input.Button, input.Kind == InputEventKind.PointerDown, updateLive: false);
                    else Touch.Apply(input);
                    LastPointerKind = input.PointerKind;
                    break;
                case InputEventKind.PointerMove:
                case InputEventKind.PointerCancel:
                    if (input.PointerKind != PointerKind.Mouse) Touch.Apply(input);
                    else if (input.Kind == InputEventKind.PointerCancel) Mouse.Cancel();
                    if (input.Kind == InputEventKind.PointerMove) LastPointerKind = input.PointerKind;
                    break;
                case InputEventKind.Scroll:
                    Mouse.HearScroll(input.ScrollY);
                    LastPointerKind = PointerKind.Mouse;
                    break;
                case InputEventKind.GamepadConnected:
                case InputEventKind.GamepadDisconnected:
                case InputEventKind.GamepadState:
                    Gamepads.Hear(input);
                    break;
            }
            EventReceived?.Invoke(input);
        }
    }

    private KeyModifiers CurrentModifiers() =>
        (Held(Key.ShiftLeft, Key.ShiftRight) ? KeyModifiers.Shift : 0) |
        (Held(Key.ControlLeft, Key.ControlRight) ? KeyModifiers.Control : 0) |
        (Held(Key.AltLeft, Key.AltRight) ? KeyModifiers.Alt : 0) |
        (Held(Key.SuperLeft, Key.SuperRight) ? KeyModifiers.Super : 0);

    private bool Held(Key left, Key right) => _eventKeys[(int)left] || _eventKeys[(int)right];

    private static bool IsModifier(Key key) => key is Key.ShiftLeft or Key.ShiftRight or Key.ControlLeft or Key.ControlRight
        or Key.AltLeft or Key.AltRight or Key.SuperLeft or Key.SuperRight;

    // The platform's clipboard, on the platform's thread only (UIKeyboard flushes it there). Nothing without a platform
    internal string ClipboardText
    {
        get => Platform?.ClipboardText ?? string.Empty;
        set
        {
            if (Platform is { } platform) platform.ClipboardText = value;
        }
    }
}
