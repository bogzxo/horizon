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
            mouse.IsDown(MouseButton.Left) || mouse.WasPressed(MouseButton.Left));
    }

    private void RouteKeyboard()
    {
        // A component that left the UI takes the focus with it.
        if (focus is not null && focus.Module is not { Enabled: true })
            focus = null;

        if (focus is null)
            return;

        // A key that edits and is being held is typed again every so often
        UIKeyboard.Repeat();

        while (focus is not null && UIKeyboard.TryRead(out char character))
            focus.OnTextInput(character);
    }

    /// <summary>
    /// Hands the mouse wheel to whatever is under the pointer that has a use for it. The innermost component
    /// first, then the ones it is inside of.
    /// </summary>
    private void RouteScroll(UIModule[] snapshot, Vector2 pointer)
    {
        float delta = UIKeyboard.TakeScroll() + Interlocked.Exchange(ref pendingScroll, 0.0f);
        if (delta == 0.0f)
            return;

        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (!snapshot[i].Enabled || !snapshot[i].Interactive)
                continue;

            // Whatever is open on top of the module is asked before what is under it.
            if (snapshot[i].Popup is { } popup && popup.PopupContains(snapshot[i].ToLocal(pointer)) && popup.OnScroll(delta))
                return;

            for (UIComponent? component = snapshot[i].FindAt(pointer); component is not null; component = component.Parent)
            {
                if (component.OnScroll(delta))
                    return;
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
