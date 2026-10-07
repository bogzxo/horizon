using System.Numerics;

using Horizon.Core;
using Horizon.Core.Components;
using Horizon.Engine;
using Horizon.Input;
using Horizon.Rendering.PostProcessing;
using Horizon.Rendering.UIX.Components;
using Horizon.Rendering.UIX.Drawing;
using Horizon.Rendering.UIX.Skinning;
using Silk.NET.Input;

namespace Horizon.Rendering.UIX;

// Where the mouse, its wheel and the keyboard are read and handed to whichever component they are meant for.
public partial class UICompositor
{
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
        // A component that left the UI takes the focus with it.
        if (focus is not null && focus.Module is not { Enabled: true })
            Focus = null;

        var keyboard = GameEngine.Instance.Input.Keyboard;

        // Escape closes whatever is open, top down: a dialog, then a popup, then the focus. One thing a press
        if (keyboard.WasPressed(Key.Escape))
            Escape(snapshot);

        // Tab walks the focus along the components that take it, back with shift. Only the UI that has it
        if (keyboard.WasPressed(Key.Tab) && focus is not null)
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
    /// Helper method for the escape key: a dialog that is up is cancelled, failing that whatever is open on top of a
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

        Focus = null;
    }

    // Scratch for walking the focus along
    private readonly List<UIComponent> focusable = [];

    /// <summary>
    /// Moves the keyboard focus to the next component that takes it (a text box), in the order of the layout and
    /// round past the end, or back with a negative step. What the tab key does. Simulation thread.
    /// </summary>
    public void FocusNext(int by = 1) => FocusNext(modules, by);

    /// <summary>
    /// Helper method to move the focus to the next (or previous) component that takes it, in the order of the layout
    /// across the modules, round past the end.
    /// </summary>
    private void FocusNext(UIModule[] snapshot, int by)
    {
        focusable.Clear();
        foreach (var module in snapshot)
        {
            if (module.Enabled && module.Interactive)
                module.CollectFocusable(focusable);
        }

        if (focusable.Count == 0)
            return;

        int index = focus is null ? -1 : focusable.IndexOf(focus);
        int next = index < 0 ? (by > 0 ? 0 : focusable.Count - 1) : ((index + by) % focusable.Count + focusable.Count) % focusable.Count;

        Focus = focusable[next];
    }

    /// <summary>
    /// Helper method to bring the tooltip of whatever the pointer has been resting on up, and take it down again the
    /// moment the pointer moves on.
    /// </summary>
    private void UpdateTooltip(UIModule[] snapshot, float dt)
    {
        UIComponent? over = hovered;
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

        // The component the pointer is over, or the nearest thing around it that has something to say
        UIComponent? showing = null;
        if (still && restingFor >= TOOLTIP_DELAY && over is not null)
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
        module?.ShowTooltip(showing!.Tooltip, module.ToLocal(lastPointer));
    }

    /// <summary>The component whose tooltip is up right now, null while none is.</summary>
    public UIComponent? TooltipOf { get; private set; }

    /// <summary>
    /// Hands the mouse wheel to whatever is under the pointer that has a use for it. The innermost component
    /// first, then the ones it is inside of.
    /// </summary>
    private void RouteScroll(UIModule[] snapshot, Vector2 pointer)
    {
        float wheel = UIKeyboard.PeekScroll();
        float delta = wheel + Interlocked.Exchange(ref pendingScroll, 0.0f);
        if (delta == 0.0f)
            return;

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (!snapshot[i].Enabled || !snapshot[i].Interactive)
                continue;

            // Whatever is open on top of the module is asked before what is under it.
            if (snapshot[i].Popup is { } popup && popup.PopupContains(snapshot[i].ToLocal(pointer)) && popup.OnScroll(delta))
            {
                if (wheel != 0.0f) UIKeyboard.UseScroll();
                return;
            }

            for (UIComponent? component = snapshot[i].FindAt(pointer); component is not null; component = component.Parent)
            {
                if (component.OnScroll(delta))
                {
                    if (wheel != 0.0f) UIKeyboard.UseScroll();
                    return;
                }
            }
        }
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

        // The other button asks whatever it's over what it has to offer: the innermost component with a handler for
        // it, then whoever listens on the whole compositor (an editor deciding by what was clicked)
        if (pointer.SecondaryDown && !secondaryWasDown)
        {
            foreach (var module in snapshot)
                module.Popup = null;

            bool handled = false;
            for (UIComponent? at = over; at is not null && !handled; at = at.Parent)
            {
                if (at.OnContextMenu is { } handler && at.EnabledInHierarchy)
                {
                    handler(at.Module!.ToLocal(pointer.Position));
                    handled = true;
                }
            }

            if (!handled)
                ContextRequested?.Invoke(over, pointer.Position);
        }
        secondaryWasDown = pointer.SecondaryDown;

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

        if (pointer.Down && !pointerWasDown)
        {
            // Pressing anywhere but on what is open closes it.
            foreach (var module in snapshot)
            {
                if (module.Popup is { } popup && popup != over)
                    module.Popup = null;
            }

            // Pressing on something that isn't part of the UI at all leaves the focus where it is,
            // or a click meant for the game would stop the typing.
            if (over is not null)
                Focus = over.Focusable && over.EnabledInHierarchy ? over : null;

            if (over is not null && over.EnabledInHierarchy)
            {
                pressed = over;
                pressed.IsPressed = true;
                pressed.OnPointerDown(pressed.Module!.ToLocal(pointer.Position));
            }
        }
        else if (pointer.Down && pressed is not null)
        {
            pressed.OnPointerDrag(pressed.Module!.ToLocal(pointer.Position));
        }
        else if (!pointer.Down && pressed is not null)
        {
            var released = pressed;
            pressed = null;

            released.IsPressed = false;
            released.OnPointerUp(released.Module!.ToLocal(pointer.Position));

            if (released == over)
                released.OnClick();
        }

        pointerWasDown = pointer.Down;
    }
}
