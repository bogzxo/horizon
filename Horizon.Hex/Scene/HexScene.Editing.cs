using System.Numerics;

using Horizon.Engine;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// Adding, deleting, moving and selecting components, and taking any of it back.
internal sealed partial class HexScene
{
    // How far the arrows move something while shift is held
    private const float BIG_NUDGE = 10.0f;

    /* Editing */

    /// <summary>
    /// Helper method for where something new goes. Into whatever is selected if that is something things go into,
    /// otherwise next to it, and onto the screen with nothing selected.
    /// </summary>
    private UIComponent? ContainerForNew() => document.Selected switch
    {
        // Tabs take it into the page that's open
        TabPanel { Selected: var page } tabs when page >= 0 && page < tabs.Children.Count => tabs.Children[page],
        TabPanel tabs => tabs,
        Panel container => container,
        { Parent: { } owner } when owner != document.Module.Root => owner,
        _ => null
    };

    private void AddComponentOf(string kind)
    {
        if (document.Add(kind, ContainerForNew()) is { } added)
        {
            browsing = false;
            Select(added);
            treeDirty = codeDirty = true;
        }
    }

    /// <summary>
    /// Helper method to move everything that's selected a place up or down the tree, among its siblings. In a stack
    /// that is where it is laid out, anywhere else it is what is drawn over what.
    /// </summary>
    private void MoveSelected(int by)
    {
        var moving = document.TopSelection();
        if (moving.Count == 0)
            return;

        if (!document.Move(moving, by))
        {
            Say(by < 0 ? "it's the first one in there already" : "it's the last one in there already");
            return;
        }

        treeDirty = codeDirty = true;
    }

    /// <summary>Helper method to take what's selected out of its container, or (inwards) put it into the one before it.</summary>
    private void MoveSelectedAcross(bool inwards)
    {
        var moving = document.TopSelection();
        if (moving.Count == 0)
            return;

        // Coming out they land after the container one by one, the last first keeps them in their order
        if (!inwards)
            moving.Reverse();

        int moved = 0;
        string problem = string.Empty;
        foreach (var component in moving)
        {
            if (inwards ? document.MoveInto(component, out string why) : document.MoveOut(component, out why))
                moved++;
            else
                problem = why;
        }

        if (moved == 0)
        {
            Say(problem, error: true);
            return;
        }

        MarkSelection();
        treeDirty = codeDirty = true;
        Say(inwards ? "moved into the container before it" : "moved out of its container");
    }

    /* The clipboard. What was copied is kept as the code that makes it, so it can be put down in another layout */

    private static string clipboard = string.Empty;

    private void CopySelected()
    {
        var copied = document.TopSelection();
        if (copied.Count == 0)
            return;

        clipboard = document.Copy(copied);
        Say(copied.Count == 1 ? $"copied {document.NameOf(copied[0])}" : $"copied {copied.Count} things");
    }

    private void CutSelected()
    {
        if (document.TopSelection().Count == 0)
            return;

        CopySelected();
        DeleteAll();
        Say("cut, paste puts it back down");
    }

    private void Paste()
    {
        if (clipboard.Length == 0)
        {
            Say("nothing has been copied yet");
            return;
        }

        var made = document.Paste(clipboard, ContainerForNew(), out string problem);
        if (made.Count == 0)
        {
            Say($"couldn't paste: {problem}", error: true);
            return;
        }

        Select(null);
        foreach (var component in made)
            document.ToggleSelected(component);

        MarkSelection();
        treeDirty = codeDirty = true;
        Say(made.Count == 1 ? $"pasted as {document.NameOf(made[0])}" : $"pasted {made.Count} things");
    }

    /* Lining things up */

    private enum Edge { Left, Center, Right, Top, Middle, Bottom }

    /// <summary>
    /// Helper method to line what's selected up. Several things line up with each other (their left edges on the one
    /// furthest left, and so on), one thing on its own lines up with the container it is in. It is done by moving
    /// them, so it does nothing to what a stack or a grid puts in its place.
    /// </summary>
    private void Align(Edge edge)
    {
        var moving = document.TopSelection();
        if (moving.Count == 0)
            return;

        UIRect against;
        if (moving.Count == 1)
        {
            UIComponent parent = moving[0].Parent ?? document.Module.Root;
            against = parent.Bounds.Shrink(parent.Padding);
        }
        else
        {
            against = moving[0].Bounds;
            foreach (var component in moving)
                against = new UIRect(Vector2.Min(against.Min, component.Bounds.Min), Vector2.Max(against.Max, component.Bounds.Max));
        }

        foreach (var component in moving)
        {
            UIRect bounds = component.Bounds;

            // Y goes up in a layout, the top is the bigger number
            component.Position += edge switch
            {
                Edge.Left => new Vector2(against.Min.X - bounds.Min.X, 0),
                Edge.Center => new Vector2(against.Center.X - bounds.Center.X, 0),
                Edge.Right => new Vector2(against.Max.X - bounds.Max.X, 0),
                Edge.Top => new Vector2(0, against.Max.Y - bounds.Max.Y),
                Edge.Middle => new Vector2(0, against.Center.Y - bounds.Center.Y),
                _ => new Vector2(0, against.Min.Y - bounds.Min.Y)
            };
        }

        codeDirty = inspectorDirty = true;
        Say(moving.Count == 1 ? "lined up with its container" : $"lined {moving.Count} things up");
    }

    /// <summary>
    /// Helper method to space three or more things evenly, the gaps between them all the same, the two at the ends
    /// staying where they are.
    /// </summary>
    private void Distribute(bool horizontally)
    {
        var moving = document.TopSelection();
        if (moving.Count < 3)
        {
            Say("select three or more things to space them out");
            return;
        }

        float Low(UIComponent component) => horizontally ? component.Bounds.Min.X : component.Bounds.Min.Y;
        float Size(UIComponent component) => horizontally ? component.Bounds.Width : component.Bounds.Height;

        moving.Sort((a, b) => Low(a).CompareTo(Low(b)));

        float span = Low(moving[^1]) + Size(moving[^1]) - Low(moving[0]);
        float gap = (span - moving.Sum(Size)) / (moving.Count - 1);

        float at = Low(moving[0]);
        foreach (var component in moving)
        {
            float by = at - Low(component);
            component.Position += horizontally ? new Vector2(by, 0) : new Vector2(0, by);
            at += Size(component) + gap;
        }

        codeDirty = inspectorDirty = true;
        Say($"spaced {moving.Count} things out {(horizontally ? "across" : "up and down")}");
    }

    private void Select(UIComponent? component)
    {
        document.Selected = component;

        // The UI marks it out itself, the same way the layout debugger marks what the pointer is over
        MarkSelection();
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
        MarkSelection();
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
        shortcuts.Update(Engine.Input.Keyboard, typing);
    }

    /// <summary>
    /// Helper method to move everything that's selected by a pixel of the layout, or by a good few with shift held.
    /// </summary>
    private void Nudge(int x, int y)
    {
        var moving = document.TopSelection();
        if (moving.Count == 0)
            return;

        var keyboard = Engine.Input.Keyboard;
        bool far = keyboard.IsDown(Silk.NET.Input.Key.ShiftLeft) || keyboard.IsDown(Silk.NET.Input.Key.ShiftRight);

        foreach (var selected in moving)
            selected.Position += new Vector2(x, y) * (far ? BIG_NUDGE : 1.0f);
        codeDirty = inspectorDirty = true;
    }
}
