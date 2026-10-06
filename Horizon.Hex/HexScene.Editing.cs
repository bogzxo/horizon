using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

namespace Horizon.Hex;

// Adding, deleting, moving and selecting components, and taking any of it back.
internal sealed partial class HexScene
{
    // How far the arrows move something while shift is held
    private const float BIG_NUDGE = 10.0f;

    /* Editing */

    private void AddComponentOf(string kind)
    {
        // Into whatever is selected if that is something things go into, otherwise next to it
        UIComponent? parent = document.Selected switch
        {
            Panel container => container,
            { Parent: { } owner } when owner != document.Module.Root => owner,
            _ => null
        };

        if (document.Add(kind, parent) is { } added)
        {
            browsing = false;
            Select(added);
            treeDirty = codeDirty = true;
        }
    }

    private void DeleteSelected()
    {
        if (document.Selected is not { } selected)
            return;

        document.Remove(selected);
        Select(null);
        treeDirty = codeDirty = true;
    }

    private void MoveSelected(int by)
    {
        if (document.Selected is not { Parent: { } parent } selected)
            return;

        int index = 0;
        foreach (var sibling in parent.Children)
        {
            if (sibling == selected)
                break;
            index++;
        }

        parent.MoveChild(selected, index + by);
        treeDirty = codeDirty = true;
    }

    private void Select(UIComponent? component)
    {
        document.Selected = component;

        // The UI marks it out itself, the same way the layout debugger marks what the pointer is over
        stage.Highlighted = component;
        treeDirty = inspectorDirty = true;
    }

    /* Undoing */

    private void Undo()
    {
        if (document.Undo())
            Restored("undone");
        else
            Say("nothing to undo");
    }

    private void Redo()
    {
        if (document.Redo())
            Restored("redone");
        else
            Say("nothing to redo");
    }

    private void Restored(string what)
    {
        // The layout was built again from what it was, everything on screen that is about it is out of date
        stage.Highlighted = document.Selected;
        tabsDirty = treeDirty = inspectorDirty = codeDirty = true;

        Say($"{what}, {document.UndoCount} more to undo");
    }

    /// <summary>
    /// Notes the layout as a step to come back to once a change has been left alone for a moment with the
    /// pointer up, and sets off whatever keys were pressed (see <see cref="BuildShortcuts"/>).
    /// </summary>
    private void UpdateHistory(float dt)
    {
        if (recordPending && !compositor.Pointer.Down && (settleTimer -= dt) <= 0.0f)
        {
            recordPending = false;
            document.Record();
        }

        // A text box of the editor, or one of the layout that is being tried out in the canvas
        bool typing = compositor.Focus is not null || stage.Focus is not null;
        shortcuts.Update(Engine.InputManager.KeyboardManager, typing);
    }

    /// <summary>
    /// Helper method to move whatever is selected by a pixel of the layout, or by a good few with shift held.
    /// </summary>
    private void Nudge(int x, int y)
    {
        if (document.Selected is not { } selected)
            return;

        var keyboard = Engine.InputManager.KeyboardManager;
        bool far = keyboard.IsKeyDown(Silk.NET.Input.Key.ShiftLeft) || keyboard.IsKeyDown(Silk.NET.Input.Key.ShiftRight);

        selected.Position += new Vector2(x, y) * (far ? BIG_NUDGE : 1.0f);
        codeDirty = inspectorDirty = true;
    }
}
