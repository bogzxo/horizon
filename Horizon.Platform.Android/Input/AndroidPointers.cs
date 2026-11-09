using System.Diagnostics;
using System.Numerics;

using Android.Views;

using Horizon.Input;

using InputEvent = Horizon.Input.InputEvent;

namespace Horizon.Platform.Android;

/// <summary>
/// Fingers, pens and a mouse on the view, as the engine's pointer events. The main thread, everything here comes from
/// the activity's dispatchTouchEvent and dispatchGenericMotionEvent.
/// <para>
/// A MotionEvent says where every pointer is by an index that shifts as fingers come and go, and names each by an id
/// that only lasts the gesture and is handed out again straight after. Neither is a good name for the engine, a
/// finger that goes up and another that comes down in the same tick under the same Android id would look like one that
/// moved. So every contact gets an id of its own when it goes down, never used again, and Android's id is only how
/// it's found while it's down.
/// </para>
/// <para>
/// Fingers and pens stay fingers and pens. Horizon.UI follows the first one down itself (taps, drags that scroll, long
/// presses) and a game reads them all from Engine.Input.Touch, so nothing here turns them into the mouse and a tap is
/// never clicked twice. <see cref="MirrorPrimaryToMouse"/> still has the primary finger drive the mouse as well, for a
/// game that only reads the mouse and wants a finger to do. A second finger never becomes the mouse, not even after
/// the first one lifts, a click shouldn't jump across the screen.
/// </para>
/// </summary>
internal sealed class AndroidPointers(Action<InputEvent> publish, Func<float, float, Vector2> toPixels)
{
    // MotionEvent.FLAG_CANCELED, set on an ACTION_POINTER_UP that was really a cancel (API 33)
    private const int FLAG_CANCELED = 0x20;

    // MotionEvent.BUTTON_* bits
    private const int BUTTON_PRIMARY = 1, BUTTON_SECONDARY = 2, BUTTON_TERTIARY = 4, BUTTON_BACK = 8, BUTTON_FORWARD = 16;

    private readonly Dictionary<int, Contact> contacts = [];
    private int primary = -1;
    private int nextId = 1;

    // The mouse's buttons held, by the engine's MouseButton number
    private int mouseButtons;

    /// <summary>Whether the primary finger is the mouse too, see the class remarks.</summary>
    public bool MirrorPrimaryToMouse { get; set; }

    /// <summary>Where the mouse is, in the pixels drawn into, for the engine's mouse to start out there.</summary>
    public Vector2 MousePosition { get; private set; }

    /// <summary>How many contacts are down right now, for a check.</summary>
    public int ContactCount => contacts.Count;

    /// <summary>A touch event of the view. True when it was ours, which is always.</summary>
    public bool Touch(MotionEvent motion)
    {
        if (IsMouse(motion)) return MouseTouch(motion);

        long stamp = Stamp(motion);
        switch (motion.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                Begin(motion, motion.ActionIndex, stamp);
                break;

            case MotionEventActions.Move:
                for (int index = 0; index < motion.PointerCount; index++) Move(motion, index, stamp);
                break;

            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                End(motion.GetPointerId(motion.ActionIndex), ((int)motion.Flags & FLAG_CANCELED) != 0, stamp, toPixels(motion.GetX(motion.ActionIndex), motion.GetY(motion.ActionIndex)));
                break;

            case MotionEventActions.Cancel:
                CancelAll();
                break;
        }

        return true;
    }

    /// <summary>A generic motion event of the view (a mouse hovering, scrolling, its other buttons). True when it was ours.</summary>
    public bool Generic(MotionEvent motion)
    {
        if (!IsMouse(motion)) return false;

        long stamp = Stamp(motion);
        Vector2 position = toPixels(motion.GetX(), motion.GetY());
        switch (motion.ActionMasked)
        {
            case MotionEventActions.HoverEnter:
            case MotionEventActions.HoverMove:
            case MotionEventActions.HoverExit:
                MoveMouse(position, stamp);
                return true;

            case MotionEventActions.Scroll:
                MousePosition = position;
                publish(new InputEvent
                {
                    Kind = InputEventKind.Scroll, PointerKind = PointerKind.Mouse, Position = position,
                    ScrollY = motion.GetAxisValue(Axis.Vscroll), Timestamp = stamp
                });
                return true;

            // The left button is the touch stream's (ACTION_DOWN and UP), only the others come this way. Consumed, or
            // an unhandled right click turns into Back
            case MotionEventActions.ButtonPress:
            case MotionEventActions.ButtonRelease:
                if (ButtonOf((int)motion.ActionButton) is { } button)
                    MouseButtonChange(button, motion.ActionMasked == MotionEventActions.ButtonPress, position, stamp);
                return true;
        }

        return false;
    }

    /// <summary>
    /// Lets go of everything down, each contact cancelled and the mouse's buttons up. For the app losing focus or going
    /// to the background, when the ups would never come.
    /// </summary>
    public void CancelAll()
    {
        long stamp = Stopwatch.GetTimestamp();
        foreach ((int android, Contact contact) in contacts.ToArray())
        {
            publish(Event(InputEventKind.PointerCancel, contact.Position, contact.Kind, contact.Id, MouseButton.Left, stamp));
            if (android == primary && MirrorPrimaryToMouse) publish(Event(InputEventKind.PointerCancel, contact.Position, PointerKind.Mouse, 0, MouseButton.Left, stamp));
        }
        contacts.Clear();
        primary = -1;

        if (mouseButtons != 0)
        {
            mouseButtons = 0;
            publish(Event(InputEventKind.PointerCancel, MousePosition, PointerKind.Mouse, 0, MouseButton.Left, stamp));
        }
    }

    private void Begin(MotionEvent motion, int index, long stamp)
    {
        int android = motion.GetPointerId(index);
        Vector2 position = toPixels(motion.GetX(index), motion.GetY(index));

        // A down for an id still held means its up got lost somewhere, it's ended first
        if (contacts.ContainsKey(android)) End(android, cancelled: true, stamp, position);

        MotionEventToolType tool = motion.GetToolType(index);
        var contact = new Contact(nextId++, tool is MotionEventToolType.Stylus or MotionEventToolType.Eraser ? PointerKind.Pen : PointerKind.Touch, position);
        if (nextId == int.MaxValue) nextId = 1;
        contacts[android] = contact;
        publish(Event(InputEventKind.PointerDown, position, contact.Kind, contact.Id, MouseButton.Left, stamp));

        if (primary < 0 && contacts.Count == 1)
        {
            primary = android;
            if (MirrorPrimaryToMouse)
            {
                MousePosition = position;
                publish(Event(InputEventKind.PointerDown, position, PointerKind.Mouse, 0, MouseButton.Left, stamp));
            }
        }
    }

    private void Move(MotionEvent motion, int index, long stamp)
    {
        int android = motion.GetPointerId(index);
        if (!contacts.TryGetValue(android, out Contact? contact)) return;

        Vector2 position = toPixels(motion.GetX(index), motion.GetY(index));
        if (position == contact.Position) return;
        contact.Position = position;
        publish(Event(InputEventKind.PointerMove, position, contact.Kind, contact.Id, MouseButton.Left, stamp));

        if (android == primary && MirrorPrimaryToMouse)
        {
            MousePosition = position;
            publish(Event(InputEventKind.PointerMove, position, PointerKind.Mouse, 0, MouseButton.Left, stamp));
        }
    }

    private void End(int android, bool cancelled, long stamp, Vector2 position)
    {
        if (!contacts.Remove(android, out Contact? contact)) return;

        InputEventKind kind = cancelled ? InputEventKind.PointerCancel : InputEventKind.PointerUp;
        publish(Event(kind, position, contact.Kind, contact.Id, MouseButton.Left, stamp));

        if (android != primary) return;
        primary = -1;
        if (!MirrorPrimaryToMouse) return;
        MousePosition = position;
        publish(Event(kind, position, PointerKind.Mouse, 0, MouseButton.Left, stamp));
    }

    // A real mouse's left button and its moves while held come as touch events with the mouse as their source
    private bool MouseTouch(MotionEvent motion)
    {
        long stamp = Stamp(motion);
        Vector2 position = toPixels(motion.GetX(), motion.GetY());
        switch (motion.ActionMasked)
        {
            case MotionEventActions.Down:
                // A press of another button alone comes as a down too, that one goes by ACTION_BUTTON_PRESS
                if (((int)motion.ButtonState & BUTTON_PRIMARY) != 0 || motion.ButtonState == 0) MouseButtonChange(MouseButton.Left, true, position, stamp);
                else MoveMouse(position, stamp);
                break;
            case MotionEventActions.Move:
                MoveMouse(position, stamp);
                break;
            case MotionEventActions.Up:
                if ((mouseButtons & (1 << (int)MouseButton.Left)) != 0) MouseButtonChange(MouseButton.Left, false, position, stamp);
                else MoveMouse(position, stamp);
                break;
            case MotionEventActions.Cancel:
                CancelAll();
                break;
        }

        return true;
    }

    private void MoveMouse(Vector2 position, long stamp)
    {
        if (position == MousePosition) return;
        MousePosition = position;
        publish(Event(InputEventKind.PointerMove, position, PointerKind.Mouse, 0, MouseButton.Left, stamp));
    }

    private void MouseButtonChange(MouseButton button, bool down, Vector2 position, long stamp)
    {
        int bit = 1 << (int)button;
        if (down == ((mouseButtons & bit) != 0)) return;
        mouseButtons = down ? mouseButtons | bit : mouseButtons & ~bit;
        MousePosition = position;
        publish(Event(down ? InputEventKind.PointerDown : InputEventKind.PointerUp, position, PointerKind.Mouse, 0, button, stamp));
    }

    private static MouseButton? ButtonOf(int button) => button switch
    {
        BUTTON_SECONDARY => MouseButton.Right,
        BUTTON_TERTIARY => MouseButton.Middle,
        BUTTON_BACK => MouseButton.Button4,
        BUTTON_FORWARD => MouseButton.Button5,
        _ => null
    };

    private static bool IsMouse(MotionEvent motion) =>
        motion.IsFromSource(InputSourceType.Mouse) && motion.GetToolType(0) == MotionEventToolType.Mouse;

    private static InputEvent Event(InputEventKind kind, Vector2 position, PointerKind pointerKind, int id, MouseButton button, long stamp) =>
        InputEvent.Pointer(kind, position, pointerKind, id, button) with { Timestamp = stamp };

    /// <summary>
    /// When the event happened in Stopwatch ticks. Its time is uptimeMillis, so it's how long ago that was taken off
    /// now, which holds whatever clock Stopwatch runs on.
    /// </summary>
    private static long Stamp(MotionEvent motion) => Stamp(motion.EventTime);

    internal static long Stamp(long eventUptimeMilliseconds)
    {
        long ago = Math.Max(0, global::Android.OS.SystemClock.UptimeMillis() - eventUptimeMilliseconds);
        return Stopwatch.GetTimestamp() - ago * Stopwatch.Frequency / 1000;
    }

    private sealed class Contact(int id, PointerKind kind, Vector2 position)
    {
        public int Id { get; } = id;
        public PointerKind Kind { get; } = kind;
        public Vector2 Position { get; set; } = position;
    }
}
