using System.Collections.Concurrent;
using System.Numerics;

using Silk.NET.Input;

namespace Horizon.Input;

/// <summary>
/// The mouse, as of the last update of the <see cref="InputManager"/>.
/// <code>
/// if (Engine.Input.Mouse.WasPressed(MouseButton.Left)) Shoot(Engine.Input.Mouse.Position);
/// </code>
/// Clicks are heard as they happen, so one that started and ended between two updates still counts, and the button is
/// down for that one update.
/// </summary>
public sealed class Mouse
{
    private const int BUTTONS = 16;

    private readonly ConcurrentQueue<(MouseButton Button, bool Down)> _heard = new();

    private readonly bool[] _down = new bool[BUTTONS];
    private readonly bool[] _pressed = new bool[BUTTONS];
    private readonly bool[] _released = new bool[BUTTONS];

    // Buttons that went down and up again between two updates, held for the update that hears of them, see Keyboard
    private readonly bool[] _tapped = new bool[BUTTONS];

    // Written by the thread of the window. A vector is two floats, which is read in one piece through this
    private long _latest;
    private float _scrolled;
    private readonly Lock _scrollLock = new();

    private bool _changed, _placed;

    /// <summary>
    /// Where the pointer is in the window, in pixels from its top left corner. With the game shown in a part of the
    /// window (the editor layout of the Skyline debugger) this is where it is in the game, as if that part were all
    /// of the window, so nothing that reads the mouse has to know.
    /// </summary>
    public Vector2 Position => (WindowPosition - ViewOrigin) * ViewScale;

    /// <summary>
    /// How far the pointer has moved since the last update.
    /// </summary>
    public Vector2 Delta => WindowDelta * ViewScale;

    /// <summary>
    /// How far the wheel was turned since the last update, away from the player is positive.
    /// </summary>
    public float Scroll => Withheld ? 0.0f : WindowScroll;

    public bool IsDown(MouseButton button) => !Withheld && HeldInWindow(button);

    /// <summary>
    /// Whether a button went down since the last update. Only true for that one update however long it is held.
    /// </summary>
    public bool WasPressed(MouseButton button) => !Withheld && PressedInWindow(button);

    /// <summary>
    /// Where the pointer is right now, as the window last heard, not as of the last update. From any thread. For
    /// whatever is drawn where the mouse is (a cursor), which a tick late is a cursor that trails the hand.
    /// </summary>
    public Vector2 LivePosition
    {
        get
        {
            long packed = Interlocked.Read(ref _latest);
            var position = new Vector2(BitConverter.Int32BitsToSingle((int)(packed >> 32)), BitConverter.Int32BitsToSingle((int)packed));
            return (position - ViewOrigin) * ViewScale;
        }
    }

    /* For whatever sits between the window and the game, which is the Skyline debugger. The game is shown through
       a part of the window and the mouse is the debugger's while it is over one of its panels */

    /// <summary>Where the pointer really is in the window, whatever part of it the game is shown in.</summary>
    internal Vector2 WindowPosition { get; private set; }

    internal Vector2 WindowDelta { get; private set; }

    internal float WindowScroll { get; private set; }

    /// <summary>The top left corner of the part of the window the game is shown in, and how much bigger the window is than that part.</summary>
    internal Vector2 ViewOrigin { get; set; }

    internal Vector2 ViewScale { get; set; } = Vector2.One;

    /// <summary>Whether the buttons and the wheel are somebody else's right now, the game sees none of them go down.</summary>
    internal bool Withheld { get; set; }

    internal bool HeldInWindow(MouseButton button) => Known(button) && _down[(int)button];

    internal bool PressedInWindow(MouseButton button) => Known(button) && _pressed[(int)button];

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
        WindowDelta = _placed ? position - WindowPosition : Vector2.Zero;
        WindowPosition = position;
        _placed = true;

        lock (_scrollLock)
        {
            WindowScroll = _scrolled;
            _scrolled = 0.0f;
        }

        if (_changed)
        {
            Array.Clear(_pressed);
            Array.Clear(_released);
            _changed = false;
        }

        // The clicks of the last update are let go of now
        for (int button = 0; button < BUTTONS; button++)
        {
            if (!_tapped[button]) continue;

            _tapped[button] = false;
            _down[button] = false;
            _released[button] = true;
            _changed = true;
        }

        while (_heard.TryDequeue(out var heard))
        {
            if (!Known(heard.Button)) continue;

            int button = (int)heard.Button;
            _changed = true;

            if (heard.Down)
            {
                if (!_down[button])
                {
                    _pressed[button] = true;
                    _down[button] = true;
                }

                _tapped[button] = false;
            }
            else if (_down[button])
            {
                // Pressed this very update, so it is held for it and let go of at the next
                if (_pressed[button]) _tapped[button] = true;
                else
                {
                    _released[button] = true;
                    _down[button] = false;
                }
            }
        }
    }
}
