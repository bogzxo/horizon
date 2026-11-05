using System.Numerics;

using Horizon.Engine;
using Horizon.Input;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI;

// Fingers. The compositor follows one contact at a time, the first that went down while nothing was held, and the
// others are left to the game in Engine.Input.Touch. A finger is a mouse that only hovers while it's down, but it can't
// say up front whether it's tapping, dragging or scrolling, so a press inside something that scrolls waits to find out.
// It goes past TouchSlop and the drag scrolls whatever it's in, it's let go of first and it was a tap, it holds still
// for LongPressTime and it's what a right click would be.
public partial class UICompositor
{
    /// <summary>
    /// How far a finger goes before a press is a drag, in the window's pixels at a display scale of one (the platform's
    /// density scales it). A finger never lands twice in the same place, a tap that wobbles a few pixels is still a tap.
    /// </summary>
    public float TouchSlop { get; set; } = 10.0f;

    /// <summary>How long a finger holds still for what a right click does (a context menu), or a tooltip where there's no menu.</summary>
    public float LongPressTime { get; set; } = 0.5f;

    /// <summary>How quickly a scroll that was let go of mid swipe slows down, a share of its speed every second.</summary>
    public float FlingFriction { get; set; } = 4.0f;

    /// <summary>The input this UI reads, the engine's unless a test hands it its own.</summary>
    internal InputManager? InputOverride { get; set; }

    private InputManager? InputOf => InputOverride ?? GameEngine.Instance?.Input;

    /// <summary>Turns the window's pixels into the camera's units, the camera's own way unless a test without a window says.</summary>
    internal Func<Vector2, Vector2>? ScreenToWorldOverride { get; set; }

    private Vector2 ToWorld(Vector2 screen) => ScreenToWorldOverride?.Invoke(screen) ?? viewportCamera.ScreenToWorld(screen);

    // The contact being followed, and where the last finger was so a finger's pointer stays put once it's lifted
    private int? touchId;
    private Vector2 touchAt;
    private PointerKind touchKind = PointerKind.Touch;

    // The gesture of the finger that's down. Where it went down and was last, how long it has held still, and what it
    // has turned out to be so far
    private Vector2 touchStart, touchLast;
    private float touchHeldFor;
    private bool touchMoved, touchLongPressed, touchConsumed, touchPanning, touchDeferred;

    // What a tap focuses when it's let go of, and the component whose tooltip a long hold brought up
    private UIComponent? touchFocus, touchTooltip;

    // What the finger scrolls, and how fast it was going when it let go
    private UIComponent? panFrom;
    private Vector2 flingVelocity;

    // The box a finger focused, which is the one the soft keyboard is up for, and whether the platform was asked for it
    private UIComponent? touchFocused;
    private bool textInputShown;

    /// <summary>Whether a finger is scrolling something right now, rather than pressing what it went down on.</summary>
    public bool IsTouchPanning => touchPanning;

    /// <summary>
    /// Helper method to read the pointer from the input, the finger being followed if there is one, the mouse otherwise.
    /// After a finger is lifted the pointer stays a finger that isn't down, so nothing is left hovered under where it was,
    /// until the mouse moves or is pressed.
    /// </summary>
    private UIPointer ReadPointer()
    {
        if (InputOf is not { } input)
            return Pointer with { Down = false, SecondaryDown = false, MiddleDown = false, Cancelled = false };

        if (ReadTouch(input) is { } touch)
            return touch;

        // A mouse that is pressing stays the pointer until it lets go, whatever a finger did meanwhile
        bool mousePressing = Pointer.Kind == PointerKind.Mouse && pointerWasDown;
        if (input.LastPointerKind != PointerKind.Mouse && !mousePressing)
            return new UIPointer(touchAt, false, Kind: touchKind, Id: touchId ?? 0);

        return ReadMouse(input.Mouse);
    }

    private UIPointer? ReadTouch(InputManager input)
    {
        // A new finger is only taken while nothing is held, a second finger never steals the first one's press, and a
        // finger doesn't take over a mouse that's dragging
        if (touchId is null && !pointerWasDown)
        {
            foreach (TouchPoint candidate in input.Touch.Points)
            {
                if (!candidate.WasPressed)
                    continue;

                touchId = candidate.Id;
                break;
            }
        }

        if (touchId is not { } id)
            return null;

        if (!input.Touch.TryGet(id, out TouchPoint point))
        {
            // Gone since the last tick, which is the tick after a tap that went down and up between two of them
            touchId = null;
            return new UIPointer(touchAt, false, Kind: touchKind, Id: id);
        }

        touchAt = ToWorld(point.Position);
        touchKind = point.Kind;
        if (point.WasCancelled)
        {
            touchId = null;
            return new UIPointer(touchAt, false, Kind: point.Kind, Id: id, Cancelled: true);
        }

        // Down and up between two ticks is down for this one, and let go of at the next (when it's gone from the list)
        bool down = !point.WasReleased || point.WasPressed;
        if (point.WasReleased && !point.WasPressed)
            touchId = null;

        return new UIPointer(touchAt, down, Kind: point.Kind, Id: id);
    }

    /// <summary>How many of the camera's units a pixel of the window is, for the slop.</summary>
    private float WorldPerPixel => (ToWorld(Vector2.UnitX) - ToWorld(Vector2.Zero)).Length();

    private float SlopInWorld
    {
        get
        {
            float density = GameEngine.Instance?.WindowManager is { } window ? MathF.Max(1.0f, window.DisplayScale) : 1.0f;
            float perPixel = WorldPerPixel;
            return TouchSlop * density * (perPixel > 0.0f && float.IsFinite(perPixel) ? perPixel : 1.0f);
        }
    }

    /// <summary>Helper method to start following a finger's gesture from where it went down.</summary>
    private void BeginTouch(UIPointer pointer, UIComponent? over)
    {
        touchStart = touchLast = pointer.Position;
        touchHeldFor = 0.0f;
        touchMoved = touchLongPressed = touchConsumed = touchPanning = touchDeferred = false;
        touchFocus = touchTooltip = null;
        panFrom = over;
        flingVelocity = Vector2.Zero;
    }

    /// <summary>
    /// Helper method for a finger that's still down. Turns it into a scroll once it has gone far enough and what it's in
    /// scrolls that way, into a context menu once it has held still long enough, or into the press of the component it's
    /// on once it's clear it's neither. True when the tick's drag is the finger's and the component gets none of it.
    /// </summary>
    private bool TouchHeld(UIModule[] snapshot, UIPointer pointer, UIComponent? over, float dt)
    {
        if (touchPanning)
        {
            Vector2 delta = pointer.Position - touchLast;
            touchLast = pointer.Position;
            if (dt > 0.0f)
                flingVelocity = Vector2.Lerp(flingVelocity, delta / dt, 0.5f);
            if (delta != Vector2.Zero)
                PanBy(delta);
            return true;
        }

        if (touchConsumed)
            return true;

        Vector2 moved = pointer.Position - touchStart;
        if (!touchMoved && moved.Length() > SlopInWorld)
        {
            touchMoved = true;
            touchLast = pointer.Position;

            // Mostly up or down inside something that scrolls that way, a scroll. Otherwise the press it was all along
            if (touchDeferred && MathF.Abs(moved.Y) >= MathF.Abs(moved.X) && PanBy(moved) is not null)
            {
                touchPanning = true;
                touchFocus = null;
                CancelPress();
                return true;
            }

            if (touchDeferred)
                DeliverDeferredPress();
            return false;
        }

        if (touchMoved)
            return false;

        touchHeldFor += dt;
        if (!touchLongPressed && touchHeldFor >= LongPressTime)
        {
            touchLongPressed = true;
            UIComponent? on = pressed ?? over;

            // What a right click would do there, and nothing else of the press
            if (OpenContext(snapshot, on, pointer.Position))
            {
                FocusVisible = false;
                touchConsumed = true;
                touchFocus = null;
                CancelPress();
                return true;
            }

            // Nothing to offer, but something to say, it's said once the finger lifts (a tooltip under a finger is unreadable)
            if (SaidBy(on) is { } saying)
            {
                touchTooltip = saying;
                touchConsumed = true;
                touchFocus = null;
                CancelPress();
                return true;
            }

            // A long press on something that only knows presses is a press, held
            if (touchDeferred)
                DeliverDeferredPress();
        }

        return touchDeferred;
    }

    /// <summary>Helper method to hand a component the press it was given, or the start of a drag of its edge.</summary>
    private static void PressAt(UIComponent component, Vector2 at)
    {
        if (component.GripAt(at) is var edges && edges != UIResize.None)
            component.BeginResize(edges, at);
        else
            component.OnPointerDown(at);
    }

    /// <summary>Helper method to hand the component a finger is on the press it was held back from, where the finger went down.</summary>
    private void DeliverDeferredPress()
    {
        touchDeferred = false;
        if (pressed is { Module: { } module } component)
            PressAt(component, module.ToLocal(touchStart));
    }

    /// <summary>Helper method to take the press away from whatever has it, without letting go of it or clicking it.</summary>
    private void CancelPress()
    {
        bool told = !touchDeferred;
        touchDeferred = false;
        if (pressed is not { } cancelled)
            return;

        pressed = null;
        cancelled.IsPressed = false;
        if (!told)
            return;

        if (cancelled.IsResizing)
            cancelled.EndResize();
        if (ReferenceEquals(gripped, cancelled))
            gripped = null;
        cancelled.OnPointerCancel();
    }

    /// <summary>Helper method for a finger the system took away. Everything of its gesture is dropped.</summary>
    private void CancelTouch()
    {
        CancelPress();
        touchPanning = touchConsumed = false;
        touchFocus = touchTooltip = null;
        flingVelocity = Vector2.Zero;
    }

    /// <summary>Helper method to give the focus to what a tap was on, now that it's a tap.</summary>
    private void ApplyTouchFocus()
    {
        if (touchFocus is not { } tapped)
            return;

        touchFocus = null;
        if (tapped.Module is null)
            return;

        Focus = FocusForPress(tapped);
        touchFocused = focus;
    }

    /// <summary>Helper method for a finger that was lifted, the tooltip a long hold asked for goes up now.</summary>
    private void EndTouch()
    {
        if (touchTooltip is { Module: { } module } saying && saying.Tooltip.Length > 0)
            PinTooltip(saying, saying.Tooltip, module.ToLocal(touchStart), saying.TooltipMarkdown);

        touchTooltip = null;
        touchFocus = null;
        touchConsumed = false;
        touchLongPressed = false;
    }

    /// <summary>Whether a component or anything it's inside of scrolls when a finger drags it.</summary>
    private static bool Pans(UIComponent component)
    {
        for (UIComponent? at = component; at is not null; at = at.Parent)
        {
            if (at.UsesScrollSpeed)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Helper method to scroll by a finger's movement (in the camera's units), the innermost thing under where it went
    /// down that can still go that way first, then what that's inside of. Returns whoever took it, null for nobody.
    /// </summary>
    private UIComponent? PanBy(Vector2 delta)
    {
        if (panFrom is not { Module: { } module } from)
            return null;

        Vector2 local = module.ToLocal(touchLast) - module.ToLocal(touchLast - delta);
        return GivePan(from, module.Popup, local);
    }

    /// <summary>
    /// Helper method to hand a finger's scroll to the innermost component that takes it, going out through what it's
    /// inside of. An open list or menu keeps it to itself, as it does the wheel.
    /// </summary>
    internal static UIComponent? GivePan(UIComponent over, UIComponent? popup, Vector2 delta)
    {
        for (UIComponent? component = over; component is not null; component = component.Parent)
        {
            if (component.EnabledInHierarchy && component.OnPan(delta))
                return component;

            if (component == popup)
                return null;
        }

        return null;
    }

    /// <summary>Helper method to keep a scroll that was let go of mid swipe going, slower every tick, until it stops or hits an end.</summary>
    private void StepFling(float dt)
    {
        if (flingVelocity == Vector2.Zero || dt <= 0.0f)
            return;

        // Once the content is past an end, its spring owns the return. Wheel-like flings used to
        // keep kicking that spring in discrete notches after the finger had already let go.
        for (UIComponent? at = panFrom; at is not null; at = at.Parent)
        {
            if (!at.HasOverscroll) continue;
            flingVelocity = Vector2.Zero;
            return;
        }

        float perPixel = WorldPerPixel > 0.0f ? WorldPerPixel : 1.0f;
        if (flingVelocity.Length() < 40.0f * perPixel || PanBy(flingVelocity * dt) is null)
        {
            flingVelocity = Vector2.Zero;
            return;
        }

        flingVelocity *= MathF.Exp(-FlingFriction * dt);
    }

    /// <summary>
    /// Helper method to have the platform's soft keyboard up while a box a finger focused has the focus, and down again
    /// once it doesn't. A box focused by a mouse or the keys has a keyboard already.
    /// </summary>
    private void UpdateTextInput()
    {
        if (focusOwner is not null && focusOwner != this)
            return;

        if (touchFocused is not null && touchFocused != focus)
            touchFocused = null;

        bool wanted = focus is { WantsTextInput: true } && focus == touchFocused;
        if (wanted == textInputShown)
            return;

        textInputShown = wanted;
        InputOf?.SetTextInputActive(wanted);
    }

    /// <summary>
    /// Lays the modules out on a screen and routes the input of one tick through them, the pointer, the keys and the
    /// tooltip, then updates them. What an update does without a renderer to paint for, so a test can drive a UI from
    /// the same queued events a platform posts.
    /// </summary>
    internal void RouteForTest(UIRect screen, UISkin skin, float dt)
    {
        // What sizes and slides things (a slider's track) goes by the skin the render thread would have loaded
        Skin ??= skin;

        var snapshot = modules;
        foreach (var module in snapshot)
        {
            module.FitTo(screen);
            module.Layout(screen, skin);
        }

        RoutePointer(snapshot, dt);
        RouteKeyboard(snapshot);
        UpdateTextInput();
        UpdateTooltip(snapshot, dt);

        foreach (var module in snapshot)
        {
            if (module.Enabled)
                module.Update(dt);
        }
    }
}
