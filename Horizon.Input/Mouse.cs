using System.Collections.Concurrent;
using System.Numerics;

using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// The mouse, as of the last update of the <see cref="InputManager"/>.
/// <code>
/// if (Engine.Input.Mouse.WasPressed(MouseButton.Left)) Shoot(Engine.Input.Mouse.Position);
/// </code>
/// Clicks are heard as they happen, so one that started and ended between two updates still counts.
/// </summary>
public sealed class Mouse
{
    private const int BUTTONS = 16;

    private readonly ConcurrentQueue<(MouseButton Button, bool Down)> _heard = new();

    private readonly bool[] _down = new bool[BUTTONS];
    private readonly bool[] _pressed = new bool[BUTTONS];
    private readonly bool[] _released = new bool[BUTTONS];

    // Written by the thread of the window. A vector is two floats, which is read in one piece through this
    private long _latest;
    private float _scrolled;
    private readonly Lock _scrollLock = new();

    private bool _changed, _placed;

    /// <summary>
    /// Where the pointer is in the window, in pixels from its top left corner.
    /// </summary>
    public Vector2 Position { get; private set; }

    /// <summary>
    /// How far the pointer has moved since the last update.
    /// </summary>
    public Vector2 Delta { get; private set; }

    /// <summary>
    /// How far the wheel was turned since the last update, away from the player is positive.
    /// </summary>
    public float Scroll { get; private set; }

    public bool IsDown(MouseButton button) => Known(button) && _down[(int)button];

    /// <summary>
    /// Whether a button went down since the last update. Only true for that one update however long it is held.
    /// </summary>
    public bool WasPressed(MouseButton button) => Known(button) && _pressed[(int)button];

    public bool WasReleased(MouseButton button) => Known(button) && _released[(int)button];

    private static bool Known(MouseButton button) => (uint)button < BUTTONS;

    internal void Attach(IInputContext context)
    {
        foreach (IMouse mouse in context.Mice)
        {
            Place(mouse.Position);

            mouse.MouseMove += (_, position) => Place(position);
            mouse.MouseDown += (_, button) => _heard.Enqueue((button, true));
            mouse.MouseUp += (_, button) => _heard.Enqueue((button, false));
            mouse.Scroll += (_, wheel) =>
            {
                lock (_scrollLock) _scrolled += wheel.Y;
            };
        }
    }

    private void Place(Vector2 position)
    {
        long packed = ((long)BitConverter.SingleToInt32Bits(position.X) << 32) | (uint)BitConverter.SingleToInt32Bits(position.Y);
        Interlocked.Exchange(ref _latest, packed);
    }

    /// <summary>
    /// Moves the mouse on to what was heard since the last time. Once an update, before anybody reads it.
    /// </summary>
    internal void Update()
    {
        long packed = Interlocked.Read(ref _latest);
        var position = new Vector2(BitConverter.Int32BitsToSingle((int)(packed >> 32)), BitConverter.Int32BitsToSingle((int)packed));

        // The first time there is nowhere it came from
        Delta = _placed ? position - Position : Vector2.Zero;
        Position = position;
        _placed = true;

        lock (_scrollLock)
        {
            Scroll = _scrolled;
            _scrolled = 0.0f;
        }

        if (_changed)
        {
            Array.Clear(_pressed);
            Array.Clear(_released);
            _changed = false;
        }

        while (_heard.TryDequeue(out var heard))
        {
            if (!Known(heard.Button)) continue;

            int button = (int)heard.Button;

            if (heard.Down && !_down[button]) _pressed[button] = true;
            else if (!heard.Down && _down[button]) _released[button] = true;

            _down[button] = heard.Down;
            _changed = true;
        }
    }
}
