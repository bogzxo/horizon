using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering.PostProcessing;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI;

// Where the mouse, its wheel and the keyboard are read and handed to whichever component they are meant for.
public partial class UICompositor
{
    /// <summary>
    /// Whether this UI reads the wheel as the window has it rather than as the game is given it. For the UI that
    /// sits between the two and withholds the mouse from the game while it is over one of its own panels.
    /// </summary>
    internal bool ReadsWindowMouse { get; set; }

    /// <summary>
    /// Whether a press on the edges of a window that has none of the system's frame drags them, the way the frame would
    /// (see <c>WindowManagerConfiguration.CustomFrame</c> and <see cref="Components.TitleBar"/>). One UI of the window
    /// says so, the one its title bar is in.
    /// </summary>
    public bool ResizesWindow { get; set; }

    /// <summary>Which edges of the window the pointer is on, none unless this UI drags them and the window has no frame.</summary>
    public WindowEdges WindowEdgeAt()
    {
        // A UI driven by something other than the mouse (a script testing it) has no business with the real window's edges
        if (!ResizesWindow || PointerSource is not null || GameEngine.Instance?.WindowManager is not { CustomFrame: true, IsMaximized: false } window)
            return WindowEdges.None;

        // As wide as the system's own on a scaled screen, an edge of a few pixels on a 4K one is a needle to thread
        return WindowManager.EdgesAt(GameEngine.Instance.Input.Mouse.WindowPosition, window.PointSize, window.ScalesToDisplay ? window.WindowUnitScale : 1.0f);
    }

    private UIPointer ReadMouse(Mouse mouse)
    {
        // A click that was over before this update even started still counts as one
        return new UIPointer(
            ToWorld(mouse.Position),
            mouse.IsDown(MouseButton.Left) || mouse.WasPressed(MouseButton.Left),
            mouse.IsDown(MouseButton.Right) || mouse.WasPressed(MouseButton.Right),
            mouse.IsDown(MouseButton.Middle) || mouse.WasPressed(MouseButton.Middle));
    }

    private void RouteKeyboard(UIModule[] snapshot)
    {
        // A component that left the UI takes the focus with it, unless it went with a window, which gives it back
        KeepKeyboardSomewhere();

        if (InputOf is not { } input)
            return;

        var keyboard = input.Keyboard;
        if (KeysMoved(keyboard))
            FocusVisible = true;

        // Escape closes whatever is open, top down. A dialog, then a popup, then the focus, one thing a press
        // A component that takes its keys raw gets them all, escape and tab among them. Only the UI whose component has
        // the keyboard says so, or whichever comes first while nobody's has. Every UI said it every tick once, and the
        // ones with nothing focused (the debugger's, the performance overlay's, which a Debug build always has) switched
        // raw keys off a moment after the terminal switched them on and threw the presses away, Return among them
        if (focusOwner == this || focusOwner is null)
            UIKeyboard.RawKeys = focus is { TakesRawKeys: true };

        // A menu that's open has the keys to itself, Escape and all
        if (RouteToPopup(snapshot, keyboard))
            return;

        if (keyboard.WasPressed(Key.Escape) && !UIKeyboard.RawKeys)
            Escape(snapshot);

        // Tab walks the focus along the components that take it, back with shift. Only the UI that has it
        if (keyboard.WasPressed(Key.Tab) && !keyboard.Control && focus is not null && !focus.KeepsTab)
            FocusNext(snapshot, keyboard.Shift ? -1 : 1);

        if (focus is null)
        {
            // Nothing is typing, so a paste is for whoever asked for those (an editor). Everything else typed into
            // the void is thrown away with it
            if (Pasted is not null && focusOwner is null)
            {
                while (UIKeyboard.TryRead(out char character))
                {
                    if (character == UIKeyboard.PASTE && UIKeyboard.TryTakePaste(out string text))
                        Pasted(text);
                }
            }

            return;
        }

        // A key that edits and is being held is typed again every so often
        UIKeyboard.Repeat();

        while (focus is { TakesRawKeys: true } && UIKeyboard.TryReadKey(out UIKeyPress press))
            focus.OnKeyDown(press);

        while (focus is not null && UIKeyboard.TryRead(out char character))
        {
            if (character == UIKeyboard.PASTE)
            {
                if (UIKeyboard.TryTakePaste(out string text))
                    focus.OnPaste(text);
                continue;
            }

            focus.OnTextInput(character);
        }
    }

    /// <summary>
    /// Helper method for the escape key. A dialog that is up is cancelled, failing that whatever is open on top of a
    /// module is closed, failing that the focus is given up. Nothing of it, and the key was for the game.
    /// </summary>
    private void Escape(UIModule[] snapshot)
    {
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (!snapshot[i].Enabled)
                continue;

            if (snapshot[i].Dialog is { } dialog)
            {
                dialog.Cancel();
                return;
            }
        }

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (snapshot[i].Enabled && snapshot[i].Popup is not null)
            {
                snapshot[i].Popup = null;
                return;
            }
        }

        // Whatever the focus is in can take it, a window of settings closes with its search box focused
        for (UIComponent? at = focus; at is not null; at = at.Parent)
        {
            if (at.OnEscape())
                return;
        }

        Focus = null;
    }

    // Scratch for walking the focus along
    private readonly List<UIComponent> focusable = [];

    /// <summary>
    /// Moves the keyboard focus to the next thing Tab stops on (a text box, a button, a dropdown), in the order of the
    /// layout and round past the end, or back with a negative step, round the window it's in when that window holds it.
    /// What the tab key does. Simulation thread.
    /// </summary>
    public void FocusNext(int by = 1) => FocusNext(modules, by);

    /// <summary>
    /// Helper method to move the focus to the next (or previous) component that takes it, in the order of the layout
    /// across the modules, round past the end.
    /// </summary>
    private void FocusNext(UIModule[] snapshot, int by)
    {
        // Inside a window that holds the keyboard, round what's in it and no further
        UIComponent? holding = null;
        for (UIComponent? at = focus; at is not null && holding is null; at = at.Parent)
        {
            if (at.HoldsTab)
                holding = at;
        }

        focusable.Clear();
        if (holding is not null)
        {
            UIModule.CollectFocusable(holding, focusable);
        }
        else
        {
            foreach (var module in snapshot)
            {
                if (module.Enabled && module.Interactive)
                    module.CollectFocusable(focusable);
            }
        }

        // Somewhere of its own to go, when that's on screen
        if (focus?.TabTo?.Invoke(by) is { Module.Enabled: true } chosen && chosen.IsShownInHierarchy && chosen.EnabledInHierarchy)
        {
            Focus = chosen;
            if (chosen is TextBox chosenBox)
                chosenBox.SelectAll();
            return;
        }

        if (focusable.Count == 0)
            return;

        int index = focus is null ? -1 : focusable.IndexOf(focus);
        int next = index < 0 ? (by > 0 ? 0 : focusable.Count - 1) : ((index + by) % focusable.Count + focusable.Count) % focusable.Count;

        Focus = focusable[next];

        // Tabbed into, a box has all of what is in it picked out, so typing replaces it without a ctrl+a first
        if (focus is TextBox box)
            box.SelectAll();
    }

    // Whether a key took the tooltip down, and where the pointer was then, it comes back once the pointer moves on
    private bool keyHushed;
    private Vector2 keyHushedAt;

    /// <summary>
    /// Helper method to bring the tooltip of whatever the pointer has been resting on up, and take it down again the
    /// moment the pointer moves on.
    /// </summary>
    private void UpdateTooltip(UIModule[] snapshot, float dt)
    {
        // One a component put up stays where it was put until it's taken down or something is clicked, the pointer can go
        // wherever it likes meanwhile
        if (pinned is { } pin)
        {
            if (pin.Owner.Module is { } owner && pin.Owner.Visible && !Pointer.Down)
            {
                if (tooltipModule is not null && tooltipModule != owner)
                    tooltipModule.ShowTooltip(null, default);
                tooltipModule = owner;
                TooltipOf = pin.Owner;
                owner.ShowTooltip(pin.Text, pin.At, pin.Markdown);
                return;
            }

            pinned = null;
            restingAt = lastPointer;
            restingFor = 0.0f;
        }

        // A key pressed takes the tooltip down until the pointer moves on, the one of the button it rests on stayed up
        // over whatever was typed after
        if (InputOf?.Keyboard is { AnyPressed: true })
        {
            keyHushedAt = lastPointer;
            keyHushed = true;
        }
        else if (keyHushed && Vector2.Distance(lastPointer, keyHushedAt) > TOOLTIP_DRIFT)
        {
            keyHushed = false;
        }

        UIComponent? over = keyHushed ? null : hovered;
        bool still = Vector2.Distance(lastPointer, restingAt) <= TOOLTIP_DRIFT && !Pointer.Down;

        if (!still)
        {
            restingAt = lastPointer;
            restingFor = 0.0f;
        }
        else
        {
            restingFor += dt;
        }

        // A click puts the tooltip of what was clicked away until the pointer goes off it, or a little way from where it
        // was clicked (the next row of a list is the same list). It came back over the Run button while the window the
        // click opened was up
        if (Pointer.Down)
            (hushed, hushedAt) = (over, lastPointer);
        else if (hushed is not null && (hushed != over || Vector2.Distance(lastPointer, hushedAt) > TOOLTIP_DRIFT * 3.0f))
            hushed = null;

        // The one up already stays while the pointer is on what it's about, over the box itself, or on its way between
        if (held is { } up)
        {
            if (!Pointer.Down && !keyHushed && up.Of != hushed && up.Of.Module is { } owner && owner == tooltipModule && up.Of.Visible)
            {
                // Something else with its own say takes it down at once, the way across is over the same thing or nothing
                UIComponent? saying = SaidBy(over);
                bool onIt = saying == up.Of && up.Of.Tooltip == up.Text;
                bool onBox = owner.TooltipBox.Width > 0.0f && owner.TooltipBox.Shrink(new UIEdges(-TOOLTIP_DRIFT * 4.0f)).Contains(owner.ToLocal(lastPointer));
                heldFor = onIt || onBox ? TOOLTIP_GRACE : saying is not null && saying != up.Of ? 0.0f : heldFor - dt;
                if (heldFor > 0.0f)
                {
                    TooltipOf = up.Of;
                    owner.ShowTooltip(up.Text, up.At, up.Markdown);
                    return;
                }
            }

            held = null;
        }

        // The component the pointer is over, or the nearest thing around it that has something to say
        UIComponent? showing = null;
        if (still && restingFor >= TOOLTIP_DELAY && over is not null && over != hushed)
        {
            showing = SaidBy(over);
        }

        UIModule? module = showing?.Module;
        if (tooltipModule is not null && tooltipModule != module)
            tooltipModule.ShowTooltip(null, default);

        tooltipModule = module;
        TooltipOf = showing;
        if (module is not null)
        {
            held = (showing!, showing.Tooltip, showing.TooltipMarkdown, module.ToLocal(lastPointer));
            heldFor = TOOLTIP_GRACE;
            module.ShowTooltip(showing.Tooltip, module.ToLocal(lastPointer), showing.TooltipMarkdown);
        }
    }

    // The module whose tooltip is up under a point (in the camera's units), null when none is there
    private UIModule? TooltipBoxAt(Vector2 point) =>
        tooltipModule is { TooltipText.Length: > 0 } module && module.TooltipBox.Width > 0.0f && module.TooltipBox.Contains(module.ToLocal(point)) ? module : null;

    // The component the pointer is over, or the nearest thing around it that has something to say
    private static UIComponent? SaidBy(UIComponent? over)
    {
        for (UIComponent? at = over; at is not null; at = at.Parent)
        {
            if (at.Tooltip.Length > 0)
                return at;
        }

        return null;
    }

    /// <summary>The component whose tooltip is up right now, null while none is.</summary>
    public UIComponent? TooltipOf { get; private set; }

    /// <summary>
    /// Puts a tooltip up at a place of a component's module (its units), not where the pointer is, until the component
    /// takes it down (<see cref="UnpinTooltip"/>), another is pinned, or the pointer moves. What a code editor says of
    /// the name at the caret when it's asked with the keyboard.
    /// </summary>
    public void PinTooltip(UIComponent owner, string text, Vector2 at, bool markdown = false) => pinned = (owner, text, at, markdown, lastPointer);

    /// <summary>Takes down the tooltip a component pinned, nothing when another's is up.</summary>
    public void UnpinTooltip(UIComponent owner)
    {
        if (pinned?.Owner == owner)
            pinned = null;
    }

    /// <summary>Whether a component has a tooltip pinned up right now.</summary>
    public bool Pinned(UIComponent owner) => pinned?.Owner == owner;

    /// <summary>The component that took the last turn of the wheel, null when nothing had a use for it. For a test.</summary>
    public UIComponent? WheelTakenBy
    {
        get => wheelTakenBy?.TryGetTarget(out UIComponent? taken) == true ? taken : null;
        private set => wheelTakenBy = value is null ? null : new WeakReference<UIComponent>(value);
    }

    // Weak, it's only for a test to look at, and it kept a closed file's whole editor alive until the wheel turned again
    private WeakReference<UIComponent>? wheelTakenBy;

    /// <summary>
    /// Hands the mouse wheel to whatever is under the pointer that has a use for it. The innermost component
    /// first, then the ones it is inside of.
    /// </summary>
    private void RouteScroll(UIModule[] snapshot, Vector2 pointer)
    {
        float wheel = ReadsWindowMouse ? UIKeyboard.PeekWindowScroll() : UIKeyboard.PeekScroll();
        float delta = wheel + Interlocked.Exchange(ref pendingScroll, 0.0f);
        if (delta == 0.0f)
            return;

        WheelTakenBy = null;
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (!snapshot[i].Enabled || !snapshot[i].Interactive)
                continue;

            // Found the way the pointer finds what it's over, what's open on top first, then what's raised, cut off where
            // it's drawn cut off. The wheel went by everything whose box held the pointer, clipped or not, so the rows of a
            // window's list running on far past its bottom took the wheel off whatever was down there
            if (snapshot[i].HitTest(pointer) is not { } over)
                continue;

            // Whatever is in front has the wheel over itself, used or not. Nothing behind it gets it, a window of settings
            // with nothing to scroll where the pointer is doesn't scroll the code under it
            WheelTakenBy = GiveWheel(over, snapshot[i].Popup, delta, ScrollSpeed);
            if (wheel != 0.0f) UIKeyboard.UseScroll();
            return;
        }
    }

    /// <summary>
    /// Helper method to hand a turn of the wheel to the innermost component that has a use for it, starting at what the
    /// pointer is over and going out through what that's inside of. Returns whoever took it, null for nobody.
    /// </summary>
    internal static UIComponent? GiveWheel(UIComponent over, UIComponent? popup, float delta, float scrollSpeed = 1)
    {
        for (UIComponent? component = over; component is not null; component = component.Parent)
        {
            if (component.OnScroll(component.UsesScrollSpeed ? delta * scrollSpeed : delta))
                return component;

            // An open list or menu that can't scroll is still on top of the rest, what it was opened from doesn't get it
            if (component == popup)
                return null;
        }

        return null;
    }

    // Closing a dialog invalidates a press begun inside it, even while its children
    // remain attached for the exit animation. Cancellation must never become a release.
    internal void CancelPointerWithin(UIComponent owner)
    {
        for (UIComponent? at = pressed; at is not null; at = at.Parent)
        {
            if (at != owner) continue;
            var cancelled = pressed!;
            pressed = null;
            cancelled.IsPressed = false;
            if (cancelled.IsResizing) cancelled.EndResize();
            if (gripped == cancelled) gripped = null;
            return;
        }
    }

    private UIComponent? releasing;
    private long chooserClickAt;
    private Vector2 chooserClickPosition;
    private bool chooserClickPending, swallowingChooserClick;

    // A one-click choice can replace its card with another picker. The second
    // half of a double click belongs to that choice, not the new card or editor.
    internal void FinishChooserClick(UIComponent list)
    {
        if (releasing != list) return; // Keyboard activation needs no pointer guard.
        chooserClickAt = Environment.TickCount64;
        chooserClickPosition = Pointer.Position;
        chooserClickPending = true;
    }

    private void RoutePointer(UIModule[] snapshot, float dt)
    {
        UIPointer pointer = PointerSource?.Invoke() ?? ReadPointer();
        lastPointer = pointer.Position;
        Pointer = pointer;

        if (chooserClickPending && (Environment.TickCount64 - chooserClickAt > 500 ||
            Vector2.DistanceSquared(pointer.Position, chooserClickPosition) > 16))
            chooserClickPending = false;
        if (chooserClickPending && pointer.Down && !pointerWasDown)
        {
            chooserClickPending = false;
            swallowingChooserClick = true;
        }
        if (swallowingChooserClick)
        {
            // Consume the entire press/release, including a drag after the second press.
            IsPointerOverUI = true;
            pointerWasDown = pointer.Down;
            secondaryWasDown = pointer.SecondaryDown;
            if (!pointer.Down) swallowingChooserClick = false;
            return;
        }

        // The system took the finger away (its own gesture, the app losing focus). Whatever it held is let go of
        // without a click, and nothing of the gesture carries on
        if (pointer.Cancelled)
        {
            CancelTouch();
            SetHovered(null);
            IsPointerOverUI = false;
            pointerWasDown = false;
            secondaryWasDown = false;
            return;
        }

        RouteScroll(snapshot, pointer.Position);
        if (!pointer.Down)
            StepFling(dt);

        // A component that left the UI while it was held is forgotten.
        if (pressed is not null && pressed.Module is not { Enabled: true })
        {
            pressed.IsPressed = false;
            pressed = null;
            touchDeferred = false;
        }

        // Modules are drawn in order, so the last one is on top and gets the first look.
        UIComponent? over = null;
        for (int i = snapshot.Length - 1; i >= 0 && over is null; i--)
        {
            if (snapshot[i].Enabled)
                over = snapshot[i].HitTest(pointer.Position);
        }

        // A plain control hint is not interactive. Once the pointer leaves its owner,
        // remove it before hit testing the tooltip box, or it can swallow the first
        // click on a neighboring control. Rich documentation and pinned tips keep
        // their reading/explicit-dismissal behavior.
        if (pinned is null && held is { Markdown: false } hint &&
            (SaidBy(over) != hint.Of || pointer.Down || pointer.SecondaryDown))
        {
            held = null;
            TooltipOf = null;
            tooltipModule?.ShowTooltip(null, default);
            tooltipModule = null;
            restingAt = pointer.Position;
            restingFor = 0;
        }

        // A tooltip is on top of everything, what's under it doesn't get the pointer and a click on it only takes it down.
        // One went straight through to the code under it and moved the caret somewhere nobody could see
        if (pressed is null && !touchPanning && TooltipBoxAt(pointer.Position) is { } tipModule)
        {
            if ((pointer.Down && !pointerWasDown) || (pointer.SecondaryDown && !secondaryWasDown))
            {
                (hushed, hushedAt) = (TooltipOf, pointer.Position);
                held = null;
                pinned = null;
                TooltipOf = null;
                tipModule.ShowTooltip(null, default);
                tooltipModule = null;
            }

            over = null;
            IsPointerOverUI = true;
        }
        else
        {
            IsPointerOverUI = over is not null || pressed is not null || touchPanning;
        }

        // The other button asks whatever it's over what it has to offer, the innermost component with a handler for
        // it, then whoever listens on the whole compositor (an editor deciding by what was clicked)
        if (pointer.SecondaryDown && !secondaryWasDown)
        {
            FocusVisible = false;
            OpenContext(snapshot, over, pointer.Position);
        }
        secondaryWasDown = pointer.SecondaryDown;

        // The edge of something that can be dragged bigger lights up under the pointer, and stays lit while it is held. What
        // was under the pointer can have left its module this very update (a window closed by escape), and has no edges then.
        // A finger has nothing hovering to light up
        UIResize grip = !pointer.IsTouch && over is { Module: { } overModule } && pressed is null ? over.GripAt(overModule.ToLocal(pointer.Position)) : UIResize.None;
        UIComponent? gripOwner = grip != UIResize.None ? over : null;
        if (!ReferenceEquals(gripped, gripOwner) || (gripOwner is not null && gripOwner.ActiveGrip != grip))
        {
            if (gripped is { IsResizing: false })
                gripped.ActiveGrip = UIResize.None;

            if (gripOwner is not null)
                gripOwner.ActiveGrip = grip;

            if (gripped is null || !gripped.IsResizing)
                gripped = gripOwner;
        }

        // A component that takes its drags by finger, or the scroll a finger's drag turns into, gets them before anything
        // below hears of the pointer
        bool touchTook = pointer.IsTouch && pointer.Down && pointerWasDown && TouchHeld(snapshot, pointer, over, dt);

        // While something is held nothing else reacts to the pointer passing over it. A finger is only over anything
        // while it's down, and not while it's scrolling, or the last thing tapped stays lit forever
        bool hovers = !pointer.IsTouch || (pointer.Down && !touchPanning && !touchConsumed);
        SetHovered(hovers && (pressed is null || pressed == over) ? over : null);

        if (touchTook)
        {
            // The finger's, scrolling or held for a menu
        }
        else if (pointer.IsTouch && touchPanning && !pointer.Down)
        {
            // Let go of mid scroll, it carries on by itself and slows down. Not a click on anything
            touchPanning = false;
        }
        // An edge of a window without the system's frame, dragged by the window itself, nothing in the UI gets the press
        else if (!pointer.IsTouch && pointer.Down && !pointerWasDown && !snapshot.Any(module => module.Dialog is { IsOpen: true }) &&
            WindowEdgeAt() is var windowEdges && windowEdges != WindowEdges.None)
        {
            GameEngine.Instance.WindowManager.BeginResize(windowEdges);
        }
        else if (pointer.Down && !pointerWasDown)
        {
            // Pressing anywhere but on what is open closes it.
            foreach (var module in snapshot)
            {
                if (module.Popup is { } popup && popup != over)
                    module.Popup = null;
            }

            // Pressing on something that isn't part of the UI at all leaves the focus where it is,
            // or a click meant for the game would stop the typing. So does a menu, which has nothing to type into.
            // A finger moves the focus when it's let go of as a tap, a finger that scrolls past a box doesn't type in it
            FocusVisible = false;
            if (pointer.IsTouch)
                BeginTouch(pointer, over);
            if (over is not null && !over.LeavesFocus)
            {
                if (pointer.IsTouch)
                    touchFocus = over;
                else
                    Focus = FocusForPress(over);
            }

            if (over is { Module: { } pressedIn } && over.EnabledInHierarchy)
            {
                pressed = over;
                pressed.IsPressed = true;

                // On an edge that can be dragged the press is the start of that, and none of the component's business.
                // Inside something a finger scrolls the component hears of the press once it's clear it's no scroll
                Vector2 at = pressedIn.ToLocal(pointer.Position);
                touchDeferred = pointer.IsTouch && !pressed.KeepsTouchDrag(at) && Pans(pressed);
                if (!touchDeferred)
                    PressAt(pressed, at);
            }
        }
        else if (pointer.Down && pressed is not null)
        {
            // What was held can leave the UI while it's held (a window closed by a key), and is let go of then
            if (pressed.Module is not { } heldIn)
            {
                pressed.IsPressed = false;
                pressed = null;
            }
            else if (touchDeferred)
            {
                // Not the component's yet, see BeginTouch
            }
            else if (pressed.IsResizing)
                pressed.DragResize(heldIn.ToLocal(pointer.Position));
            else
                pressed.OnPointerDrag(heldIn.ToLocal(pointer.Position));
        }
        else if (!pointer.Down && pressed is not null)
        {
            // A tap. What it's on hears of the press now, then has the focus, then is let go of and clicked as the mouse's would be
            if (pointer.IsTouch)
            {
                ApplyTouchFocus();
                if (touchDeferred)
                    DeliverDeferredPress();
            }

            var released = pressed;
            pressed = null;

            if (released is not null)
            {
                released.IsPressed = false;

                if (released.IsResizing)
                {
                    // Letting go of an edge is not a click on whatever the edge belongs to
                    released.EndResize();
                    if (ReferenceEquals(gripped, released)) gripped = null;
                }
                else if (released.Module is { } releasedIn)
                {
                    releasing = released;
                    try { released.OnPointerUp(releasedIn.ToLocal(pointer.Position)); }
                    finally { releasing = null; }

                    if (released == over)
                        released.OnClick();
                }
            }
        }
        else if (pointer.IsTouch && !pointer.Down && pointerWasDown)
        {
            // A tap on nothing the UI has, a box focused by it still gets the focus
            ApplyTouchFocus();
        }

        if (pointer.IsTouch && !pointer.Down && pointerWasDown)
            EndTouch();

        pointerWasDown = pointer.Down;
    }

    // Lights up whatever the pointer is over, and puts out what it was over before
    private void SetHovered(UIComponent? hover)
    {
        if (hover == hovered)
            return;

        if (hovered is not null)
            hovered.IsHovered = false;
        if (hover is not null)
            hover.IsHovered = true;

        hovered = hover;
    }

    /// <summary>
    /// Helper method to ask whatever is under a point what it has to offer, the innermost component with a handler for it,
    /// then whoever listens on the whole compositor. True when somebody had something, for a finger held still that
    /// shouldn't click as well when it does.
    /// </summary>
    private bool OpenContext(UIModule[] snapshot, UIComponent? over, Vector2 position)
    {
        foreach (var module in snapshot)
            module.Popup = null;

        for (UIComponent? at = over; at is not null; at = at.Parent)
        {
            if (at.OnContextMenu is { } handler && at.EnabledInHierarchy && at.Module is { } menuModule)
            {
                handler(menuModule.ToLocal(position));
                return true;
            }
        }

        if (ContextRequested is not { } requested)
            return false;

        requested(over, position);
        return true;
    }
}
