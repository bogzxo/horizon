using System.Collections.Concurrent;

using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// The keyboard, as of the last update of the <see cref="InputManager"/>.
/// <code>
/// if (Engine.Input.Keyboard.WasPressed(Key.Space)) Jump();
/// if (Engine.Input.Keyboard.IsDown(Key.A)) MoveLeft(dt);
/// </code>
/// Keys are heard as they happen rather than looked at once an update, so a key that was tapped and let go of between
/// two updates still counts as pressed. Meant to be read from the updates, not while drawing.
/// </summary>
public sealed class Keyboard
{
    // Room for every key the window system knows, with some to spare
    private const int KEYS = 512;

    // What the window heard since the last update, which arrives on its own thread
    private readonly ConcurrentQueue<(Key Key, bool Down)> _heard = new();

    private readonly bool[] _down = new bool[KEYS];
    private readonly bool[] _pressed = new bool[KEYS];
    private readonly bool[] _released = new bool[KEYS];

    private bool _changed;

    /// <summary>
    /// Whether a key is held down right now.
    /// </summary>
    public bool IsDown(Key key) => Known(key) && _down[(int)key];

    /// <summary>
    /// Whether a key went down since the last update. Only true for that one update however long it is held.
    /// </summary>
    public bool WasPressed(Key key) => Known(key) && _pressed[(int)key];

    /// <summary>
    /// Whether a key was let go of since the last update.
    /// </summary>
    public bool WasReleased(Key key) => Known(key) && _released[(int)key];

    public bool Shift => IsDown(Key.ShiftLeft) || IsDown(Key.ShiftRight);

    public bool Control => IsDown(Key.ControlLeft) || IsDown(Key.ControlRight);

    public bool Alt => IsDown(Key.AltLeft) || IsDown(Key.AltRight);

    /// <summary>
    /// Whether any key at all went down since the last update, for a "press any key".
    /// </summary>
    public bool AnyPressed { get; private set; }

    /// <summary>
    /// -1, 0 or 1 out of two keys that push opposite ways, so <c>Axis(Key.A, Key.D)</c> is which way somebody is steering.
    /// </summary>
    public float Axis(Key negative, Key positive) => (IsDown(positive) ? 1.0f : 0.0f) - (IsDown(negative) ? 1.0f : 0.0f);

    private static bool Known(Key key) => (uint)key < KEYS;

    internal void Attach(IInputContext context)
    {
        foreach (IKeyboard keyboard in context.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) => _heard.Enqueue((key, true));
            keyboard.KeyUp += (_, key, _) => _heard.Enqueue((key, false));
        }
    }

    /// <summary>
    /// Moves the keyboard on to what was heard since the last time. Once an update, before anybody reads it.
    /// </summary>
    internal void Update()
    {
        // Nothing was pressed or let go of last time, so there is nothing to forget
        if (_changed)
        {
            Array.Clear(_pressed);
            Array.Clear(_released);
            _changed = false;
        }

        AnyPressed = false;

        while (_heard.TryDequeue(out var heard))
        {
            if (!Known(heard.Key)) continue;

            int key = (int)heard.Key;

            // A key that is held down says so over and over, only the first time is a press
            if (heard.Down && !_down[key])
            {
                _pressed[key] = true;
                AnyPressed = true;
            }
            else if (!heard.Down && _down[key])
            {
                _released[key] = true;
            }

            _down[key] = heard.Down;
            _changed = true;
        }
    }
}
