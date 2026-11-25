using System.Collections.Concurrent;
using System.Numerics;

using Horizon.Platform;

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

    private readonly ConcurrentQueue<(MouseButton Button, bool Down, bool Cancel)> _heard = new();

    private readonly bool[] _down = new bool[BUTTONS];
    private readonly bool[] _pressed = new bool[BUTTONS];
    private readonly bool[] _released = new bool[BUTTONS];

    // Buttons that went down and up again between two updates, held for the update that hears of them, see Keyboard
    private readonly bool[] _tapped = new bool[BUTTONS];

    // Written by the thread of the window. A vector is two floats, which is read in one piece through this
    private long _latest;
    private int _liveButtons;
    private float _scrolled;
    private readonly Lock _scrollLock = new();

    private bool _changed, _placed, _captured;

    // The platform's pointer and how to get something done on its thread, which is the only one that may change the cursor
    private IPlatformInput? _platform;
    private Action<Action>? _toWindow;
    private Action? _applyCapture;

    /// <summary>
    /// Whether the pointer is locked to the window and hidden, for a game that looks around with the mouse (anything
    /// first person). While it is, <see cref="Delta"/> is how far the mouse was moved, with no edge of the window or
    /// the screen to stop it, raw from the device where the system has that. <see cref="Position"/> means nothing
    /// then. From any thread, it is done on the window's the next time it looks, and the first update after it
    /// changes has no movement, or the pointer jumping to the middle reads as a flick of the wrist.
    /// </summary>
    public bool Captured
    {
        get => _captured;
        set
        {
            if (_captured == value) return;

            _captured = value;
            _placed = false;
            if (_applyCapture is not null) _toWindow?.Invoke(_applyCapture);
        }
    }

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

    /// <summary>
    /// <see cref="Scroll"/> as notches of a wheel (fractional for a trackpad's), for what zooms or steps with it. <see cref="Scroll"/>
    /// itself is in SDL's units, which are a tenth of a point on a Mac's trackpad.
    /// </summary>
    public float ScrollNotches => Withheld ? 0.0f : WindowScrollPrecise ? PreciseScroll.ToNotches(WindowScroll) : WindowScroll;

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

    /// <summary>Horizontal wheel or trackpad turn since the last update, positive to the right.</summary>
    internal float WindowScrollX { get; private set; }

    /// <summary>Whether what was heard since the last update came from a continuous device, see <see cref="PreciseScroll"/>.</summary>
    internal bool WindowScrollPrecise { get; private set; }

    /// <summary>The pinch of a trackpad since the last update, as the change of size (0.05 is five per cent bigger), 0 anywhere else.</summary>
    public float Magnify => Withheld ? 0.0f : WindowMagnify;

    /// <summary>Whether a smart zoom (a two finger double tap of a trackpad) came since the last update.</summary>
    public bool SmartMagnify => !Withheld && WindowSmartMagnify;

    /// <summary>The multi finger swipes of a trackpad since the last update, to the left minus to the right.</summary>
    public int Swipe => Withheld ? 0 : WindowSwipe;

    /// <summary>The pinch since the last update, as the change of size (0.05 is five per cent bigger).</summary>
    internal float WindowMagnify { get; private set; }

    /// <summary>Whether a smart zoom (two finger double tap) came since the last update.</summary>
    internal bool WindowSmartMagnify { get; private set; }

    /// <summary>The swipes since the last update, to the left minus to the right.</summary>
    internal int WindowSwipe { get; private set; }

    private float _magnified;
    private bool _smartMagnified;
    private int _swiped;
    private float _scrolledX;
    private bool _scrolledPrecise;
    private bool _fingersOn;
    private bool _gliding;

    /// <summary>Whether a trackpad's fingers are on it, scrolling, as of the last update (a Mac says, elsewhere always false).</summary>
    internal bool WindowFingersOn { get; private set; }

    /// <summary>Whether what was turned since the last update was the glide after a flick, the system's momentum.</summary>
    internal bool WindowScrollGliding { get; private set; }

    /// <summary>The top left corner of the part of the window the game is shown in, and how much bigger the window is than that part.</summary>
    internal Vector2 ViewOrigin { get; set; }

    internal Vector2 ViewScale { get; set; } = Vector2.One;

    /// <summary>Whether the buttons and the wheel are somebody else's right now, the game sees none of them go down.</summary>
    internal bool Withheld { get; set; }

    internal bool HeldInWindow(MouseButton button) => Known(button) && _down[(int)button];

    internal bool PressedInWindow(MouseButton button) => Known(button) && _pressed[(int)button];

    public bool WasReleased(MouseButton button) => Known(button) && _released[(int)button];

    /// <summary>Whether the window currently reports a held button, without waiting for the next input update.</summary>
    public bool IsLiveDown(MouseButton button) => Known(button) && (Volatile.Read(ref _liveButtons) & (1 << (int)button)) != 0;

    private static bool Known(MouseButton button) => (uint)button < BUTTONS;

    /// <param name="platform">The platform's pointer, where it is now in the pixels that are drawn into and how it's captured.</param>
    /// <param name="toWindow">Has something done on the platform's thread, the only one that may change the cursor.</param>
    internal void Attach(IPlatformInput platform, Action<Action>? toWindow = null)
    {
        _platform = platform;
        _toWindow = toWindow;
        _applyCapture = ApplyCapture;

        // A Mac says where the pointer is in points, the platform hands it over in the pixels that are drawn into
        Place(platform.PointerPosition);
    }

    /// <summary>Helper method to lock the pointer or let it go, as <see cref="Captured"/> says. Thread of the window.</summary>
    private void ApplyCapture() => _platform?.SetPointerCaptured(_captured);

    internal void SetLiveButton(MouseButton button, bool down)
    {
        if (!Known(button)) return;
        int bit = 1 << (int)button;
        if (down) Interlocked.Or(ref _liveButtons, bit);
        else Interlocked.And(ref _liveButtons, ~bit);
    }

    internal void HearButton(MouseButton button, bool down, bool updateLive = true)
    {
        if (updateLive) SetLiveButton(button, down);
        _heard.Enqueue((button, down, false));
    }

    internal void CancelLiveButtons() => Interlocked.Exchange(ref _liveButtons, 0);

    internal void Cancel()
    {
        _heard.Enqueue((default, false, true));
        ReleaseFingers();
    }

    internal void HearScroll(float amount, float across = 0.0f, bool precise = false, ScrollPhase phase = ScrollPhase.None)
    {
        lock (_scrollLock)
        {
            _scrolled += amount;
            _scrolledX += across;
            _scrolledPrecise |= precise;
            switch (phase)
            {
                case ScrollPhase.Touching:
                    _fingersOn = true;
                    break;
                case ScrollPhase.Momentum:
                    _fingersOn = false;
                    _gliding = true;
                    break;
                case ScrollPhase.Released:
                case ScrollPhase.MomentumEnded:
                    _fingersOn = false;
                    break;
            }
        }
    }

    /// <summary>Lets go of a trackpad's fingers that were never said to come off (the window lost the pointer).</summary>
    internal void ReleaseFingers()
    {
        lock (_scrollLock) _fingersOn = false;
    }

    internal void HearMagnify(float amount)
    {
        lock (_scrollLock) _magnified += amount;
    }

    internal void HearSmartMagnify()
    {
        lock (_scrollLock) _smartMagnified = true;
    }

    internal void HearSwipe(int direction)
    {
        lock (_scrollLock) _swiped += direction;
    }

    internal void Place(Vector2 position)
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
            WindowScrollX = _scrolledX;
            WindowScrollPrecise = _scrolledPrecise;
            _scrolled = 0.0f;
            _scrolledX = 0.0f;
            _scrolledPrecise = false;
            WindowFingersOn = _fingersOn;
            WindowScrollGliding = _gliding;
            _gliding = false;
            WindowMagnify = _magnified;
            WindowSmartMagnify = _smartMagnified;
            WindowSwipe = _swiped;
            _magnified = 0.0f;
            _smartMagnified = false;
            _swiped = 0;
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
            if (heard.Cancel)
            {
                for (int index = 0; index < BUTTONS; index++)
                {
                    if (_down[index]) _released[index] = true;
                }
                Array.Clear(_down);
                Array.Clear(_pressed);
                Array.Clear(_tapped);
                _changed = true;
                continue;
            }
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
