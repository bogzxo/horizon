using System.Diagnostics;
using System.Numerics;

using CoreGraphics;

using Foundation;

using Horizon.Input;

using UIKit;

using InputEvent = Horizon.Input.InputEvent;

namespace Horizon.Platform.iOS;

/// <summary>
/// Fingers, a Pencil and an iPad's trackpad or mouse on the view, as the engine's pointer events. The main thread,
/// everything here comes from the view's touches and its hover recognizer.
/// <para>
/// UIKit hands the same UITouch object over for a finger from the moment it goes down until it lifts, so that's how a
/// contact is found while it's down. The engine gets an id of its own for every contact, never used again, the same
/// as the Android host gives it.
/// </para>
/// <para>
/// Fingers and pens stay fingers and pens, Horizon.UI follows the first one down itself and a game reads them all from
/// Engine.Input.Touch. <see cref="MirrorPrimaryToMouse"/> has the primary finger drive the mouse as well, for a game
/// that only reads the mouse. A trackpad or mouse (UITouchType.IndirectPointer) is the mouse and nothing else.
/// </para>
/// </summary>
internal sealed class IosPointers(Action<InputEvent> publish, Func<Vector2, Vector2> toPixels)
{
    /// <summary>Where a touch is in its gesture, which of the view's four callbacks it came through.</summary>
    public enum Phase { Began, Moved, Ended, Cancelled }

    private readonly Dictionary<nint, Contact> contacts = [];
    private nint primary;
    private int nextId = 1;
    private bool mouseDown;

    /// <summary>Whether the primary finger is the mouse too, see the class remarks.</summary>
    public bool MirrorPrimaryToMouse { get; set; }

    /// <summary>Where the mouse is, in the pixels drawn into, for the engine's mouse to start out there.</summary>
    public Vector2 MousePosition { get; private set; }

    /// <summary>How many contacts are down right now, for a check.</summary>
    public int ContactCount => contacts.Count;

    /// <summary>Touches of the view in one of their phases.</summary>
    public void Touches(UIView view, NSSet touches, Phase phase)
    {
        foreach (UITouch touch in touches.ToArray<UITouch>())
        {
            CGPoint point = touch.LocationInView(view);
            Vector2 position = toPixels(new Vector2((float)point.X, (float)point.Y));
            long stamp = Stamp(touch.Timestamp);

            if (touch.Type == UITouchType.IndirectPointer)
            {
                Mouse(phase, position, stamp);
                continue;
            }

            switch (phase)
            {
                case Phase.Began:
                    Begin(touch, position, stamp);
                    break;
                case Phase.Moved:
                    Move(touch.Handle, position, stamp);
                    break;
                case Phase.Ended:
                    End(touch.Handle, cancelled: false, position, stamp);
                    break;
                case Phase.Cancelled:
                    End(touch.Handle, cancelled: true, position, stamp);
                    break;
            }
        }
    }

    /// <summary>The trackpad or mouse moved with nothing pressed, a place in the view in points.</summary>
    public void Hover(CGPoint point)
    {
        Vector2 position = toPixels(new Vector2((float)point.X, (float)point.Y));
        if (position == MousePosition) return;
        MousePosition = position;
        publish(Event(InputEventKind.PointerMove, position, PointerKind.Mouse, 0, Stopwatch.GetTimestamp()));
    }

    /// <summary>
    /// Lets go of everything down, each contact cancelled and the mouse's button up. For the app losing focus or going
    /// to the background, when the ends would never come.
    /// </summary>
    public void CancelAll()
    {
        long stamp = Stopwatch.GetTimestamp();
        foreach ((nint touch, Contact contact) in contacts.ToArray())
        {
            publish(Event(InputEventKind.PointerCancel, contact.Position, contact.Kind, contact.Id, stamp));
            if (touch == primary && MirrorPrimaryToMouse) publish(Event(InputEventKind.PointerCancel, contact.Position, PointerKind.Mouse, 0, stamp));
        }
        contacts.Clear();
        primary = 0;

        if (mouseDown)
        {
            mouseDown = false;
            publish(Event(InputEventKind.PointerCancel, MousePosition, PointerKind.Mouse, 0, stamp));
        }
    }

    private void Begin(UITouch touch, Vector2 position, long stamp)
    {
        nint key = touch.Handle;

        // A begin for a touch still held means its end got lost somewhere, it's ended first
        if (contacts.ContainsKey(key)) End(key, cancelled: true, position, stamp);

        var contact = new Contact(nextId++, touch.Type == UITouchType.Stylus ? PointerKind.Pen : PointerKind.Touch, position);
        if (nextId == int.MaxValue) nextId = 1;
        contacts[key] = contact;
        publish(Event(InputEventKind.PointerDown, position, contact.Kind, contact.Id, stamp));

        if (primary == 0 && contacts.Count == 1)
        {
            primary = key;
            if (MirrorPrimaryToMouse)
            {
                MousePosition = position;
                publish(Event(InputEventKind.PointerDown, position, PointerKind.Mouse, 0, stamp));
            }
        }
    }

    private void Move(nint key, Vector2 position, long stamp)
    {
        if (!contacts.TryGetValue(key, out Contact? contact) || position == contact.Position) return;
        contact.Position = position;
        publish(Event(InputEventKind.PointerMove, position, contact.Kind, contact.Id, stamp));

        if (key == primary && MirrorPrimaryToMouse)
        {
            MousePosition = position;
            publish(Event(InputEventKind.PointerMove, position, PointerKind.Mouse, 0, stamp));
        }
    }

    private void End(nint key, bool cancelled, Vector2 position, long stamp)
    {
        if (!contacts.Remove(key, out Contact? contact)) return;

        InputEventKind kind = cancelled ? InputEventKind.PointerCancel : InputEventKind.PointerUp;
        publish(Event(kind, position, contact.Kind, contact.Id, stamp));

        if (key != primary) return;
        primary = 0;
        if (!MirrorPrimaryToMouse) return;
        MousePosition = position;
        publish(Event(kind, position, PointerKind.Mouse, 0, stamp));
    }

    // A trackpad click or a mouse's button, which UIKit sends as a touch of its own. Only the left one comes this way
    private void Mouse(Phase phase, Vector2 position, long stamp)
    {
        MousePosition = position;
        switch (phase)
        {
            case Phase.Began when !mouseDown:
                mouseDown = true;
                publish(Event(InputEventKind.PointerDown, position, PointerKind.Mouse, 0, stamp));
                break;
            case Phase.Moved:
                publish(Event(InputEventKind.PointerMove, position, PointerKind.Mouse, 0, stamp));
                break;
            case Phase.Ended or Phase.Cancelled when mouseDown:
                mouseDown = false;
                publish(Event(phase == Phase.Ended ? InputEventKind.PointerUp : InputEventKind.PointerCancel, position, PointerKind.Mouse, 0, stamp));
                break;
        }
    }

    private static InputEvent Event(InputEventKind kind, Vector2 position, PointerKind pointerKind, int id, long stamp) =>
        InputEvent.Pointer(kind, position, pointerKind, id, MouseButton.Left) with { Timestamp = stamp };

    /// <summary>
    /// When the event happened in Stopwatch ticks. UIKit's times are seconds of system uptime, so it's how long ago that
    /// was taken off now, which holds whatever clock Stopwatch runs on.
    /// </summary>
    internal static long Stamp(double uptimeSeconds)
    {
        double ago = Math.Max(0.0, NSProcessInfo.ProcessInfo.SystemUptime - uptimeSeconds);
        return Stopwatch.GetTimestamp() - (long)(ago * Stopwatch.Frequency);
    }

    private sealed class Contact(int id, PointerKind kind, Vector2 position)
    {
        public int Id { get; } = id;
        public PointerKind Kind { get; } = kind;
        public Vector2 Position { get; set; } = position;
    }
}
