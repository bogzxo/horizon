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

    // Keys that went down and up again between two updates. They are held for the update that hears of them and let
    // go of at the next, so a tap is a tap however short it was, also for whoever only ever asks whether a key is down
    private readonly bool[] _tapped = new bool[KEYS];
    private readonly List<int> _taps = [];

    private bool _changed;

    /// <summary>
    /// Whether a key is held down right now.
    /// </summary>
    public bool IsDown(Key key) => !Withheld && Known(key) && _down[(int)key];

    /// <summary>
    /// Whether the keys are somebody else's right now (the Skyline debugger, with something being typed into one of
    /// its boxes), the game hears none of them.
    /// </summary>
    internal bool Withheld { get; set; }

    /// <summary>Whether a key went down since the last update, withheld or not. For whoever is withholding them.</summary>
    internal bool PressedRegardless(Key key) => Known(key) && _pressed[(int)key];

    internal bool DownRegardless(Key key) => Known(key) && _down[(int)key];

    /// <summary>
    /// Whether a key went down since the last update. Only true for that one update however long it is held.
    /// </summary>
    public bool WasPressed(Key key) => !Withheld && Known(key) && _pressed[(int)key];

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
    public bool AnyPressed => !Withheld && _anyPressed;

    private bool _anyPressed;

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

        _anyPressed = false;

        // The taps of the last update are let go of now
        foreach (int key in _taps)
        {
            if (!_tapped[key]) continue;

            _tapped[key] = false;
            _down[key] = false;
            _released[key] = true;
            _changed = true;
        }
        _taps.Clear();

        while (_heard.TryDequeue(out var heard))
        {
            if (!Known(heard.Key)) continue;

            int key = (int)heard.Key;
            _changed = true;

            if (heard.Down)
            {
                // A key that is held down says so over and over, only the first time is a press
                if (!_down[key])
                {
                    _pressed[key] = true;
                    _down[key] = true;
                    _anyPressed = true;
                }

                // Down again before the tap was let go of, so it isn't let go of
                _tapped[key] = false;
            }
            else if (_down[key])
            {
                if (_pressed[key])
                {
                    // Pressed this very update, so it is held for it and let go of at the next
                    if (!_tapped[key])
                    {
                        _tapped[key] = true;
                        _taps.Add(key);
                    }
                }
                else
                {
                    _released[key] = true;
                    _down[key] = false;
                }
            }
        }
    }
}
