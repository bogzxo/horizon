using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering.PostProcessing;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;
using Silk.NET.Input;

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
        var size = window.Window.Size;
        return WindowManager.EdgesAt(GameEngine.Instance.Input.Mouse.WindowPosition, new Vector2(size.X, size.Y), window.ScalesToDisplay ? window.WindowUnitScale : 1.0f);
    }

    private UIPointer ReadMouse()
    {
        var mouse = GameEngine.Instance.Input.Mouse;

        // A click that was over before this update even started still counts as one
        return new UIPointer(
            viewportCamera.ScreenToWorld(mouse.Position),
            mouse.IsDown(MouseButton.Left) || mouse.WasPressed(MouseButton.Left),
            mouse.IsDown(MouseButton.Right) || mouse.WasPressed(MouseButton.Right));
    }

    private void RouteKeyboard(UIModule[] snapshot)
    {
        // A component that left the UI takes the focus with it, unless it went with a window, which gives it back
        KeepKeyboardSomewhere();

        var keyboard = GameEngine.Instance.Input.Keyboard;
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
        // One a component put up stays where it was put until it's taken down or the pointer goes somewhere
        if (pinned is { } pin)
        {
            if (pin.Owner.Module is { } owner && pin.Owner.Visible && Vector2.Distance(lastPointer, pin.Pointer) <= TOOLTIP_DRIFT * 4.0f)
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
        if (GameEngine.Instance?.Input.Keyboard is { AnyPressed: true })
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

        // The component the pointer is over, or the nearest thing around it that has something to say
        UIComponent? showing = null;
        if (still && restingFor >= TOOLTIP_DELAY && over is not null && over != hushed)
        {
            for (UIComponent? at = over; at is not null && showing is null; at = at.Parent)
            {
                if (at.Tooltip.Length > 0)
                    showing = at;
            }
        }

        UIModule? module = showing?.Module;
        if (tooltipModule is not null && tooltipModule != module)
            tooltipModule.ShowTooltip(null, default);

        tooltipModule = module;
        TooltipOf = showing;
        module?.ShowTooltip(showing!.Tooltip, module.ToLocal(lastPointer), showing.TooltipMarkdown);
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
    public UIComponent? WheelTakenBy { get; private set; }

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

    private void RoutePointer(UIModule[] snapshot)
    {
        UIPointer pointer = PointerSource?.Invoke() ?? ReadMouse();
        lastPointer = pointer.Position;
        Pointer = pointer;

        RouteScroll(snapshot, pointer.Position);

        // A component that left the UI while it was held is forgotten.
        if (pressed is not null && pressed.Module is not { Enabled: true })
        {
            pressed.IsPressed = false;
            pressed = null;
        }

        // Modules are drawn in order, so the last one is on top and gets the first look.
        UIComponent? over = null;
        for (int i = snapshot.Length - 1; i >= 0 && over is null; i--)
        {
            if (snapshot[i].Enabled)
                over = snapshot[i].HitTest(pointer.Position);
        }

        IsPointerOverUI = over is not null || pressed is not null;

        // The other button asks whatever it's over what it has to offer, the innermost component with a handler for
        // it, then whoever listens on the whole compositor (an editor deciding by what was clicked)
        if (pointer.SecondaryDown && !secondaryWasDown)
        {
            FocusVisible = false;
            foreach (var module in snapshot)
                module.Popup = null;

            bool handled = false;
            for (UIComponent? at = over; at is not null && !handled; at = at.Parent)
            {
                if (at.OnContextMenu is { } handler && at.EnabledInHierarchy && at.Module is { } menuModule)
                {
                    handler(menuModule.ToLocal(pointer.Position));
                    handled = true;
                }
            }

            if (!handled)
                ContextRequested?.Invoke(over, pointer.Position);
        }
        secondaryWasDown = pointer.SecondaryDown;

        // The edge of something that can be dragged bigger lights up under the pointer, and stays lit while it is held. What
        // was under the pointer can have left its module this very update (a window closed by escape), and has no edges then
        UIResize grip = over is { Module: { } overModule } && pressed is null ? over.GripAt(overModule.ToLocal(pointer.Position)) : UIResize.None;
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

        // While something is held nothing else reacts to the pointer passing over it.
        UIComponent? hover = pressed is null || pressed == over ? over : null;
        if (hover != hovered)
        {
            if (hovered is not null)
                hovered.IsHovered = false;
            if (hover is not null)
                hover.IsHovered = true;

            hovered = hover;
        }

        // An edge of a window without the system's frame, dragged by the window itself, nothing in the UI gets the press
        if (pointer.Down && !pointerWasDown && WindowEdgeAt() is var windowEdges && windowEdges != WindowEdges.None)
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
            // or a click meant for the game would stop the typing. So does a menu, which has nothing to type into
            FocusVisible = false;
            if (over is not null && !over.LeavesFocus)
                Focus = FocusForPress(over);

            if (over is { Module: { } pressedIn } && over.EnabledInHierarchy)
            {
                pressed = over;
                pressed.IsPressed = true;

                // On an edge that can be dragged the press is the start of that, and none of the component's business
                Vector2 at = pressedIn.ToLocal(pointer.Position);
                if (pressed.GripAt(at) is var edges && edges != UIResize.None)
                    pressed.BeginResize(edges, at);
                else
                    pressed.OnPointerDown(at);
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
            else if (pressed.IsResizing)
                pressed.DragResize(heldIn.ToLocal(pointer.Position));
            else
                pressed.OnPointerDrag(heldIn.ToLocal(pointer.Position));
        }
        else if (!pointer.Down && pressed is not null)
        {
            var released = pressed;
            pressed = null;

            released.IsPressed = false;

            if (released.IsResizing)
            {
                // Letting go of an edge is not a click on whatever the edge belongs to
                released.EndResize();
                if (ReferenceEquals(gripped, released)) gripped = null;
            }
            else if (released.Module is { } releasedIn)
            {
                released.OnPointerUp(releasedIn.ToLocal(pointer.Position));

                if (released == over)
                    released.OnClick();
            }
        }

        pointerWasDown = pointer.Down;
    }
}
