using System.Numerics;

using Horizon.Rendering.UIX;
using Horizon.Rendering.UIX.Components;

using Key = Silk.NET.Input.Key;

namespace Horizon.Hex;

// Selecting more than one thing, groups, copies, what goes on top of what, and the right click menu for all of it.
internal sealed partial class HexScene
{
    // The menu a right click opened last, for the self-test to find its items
    private ContextMenu? contextMenu;

    // Ctrl or shift held, as far as clicks go. The self-test can't hold keys, so it says so here instead
    private bool fakeModifier;

    /// <summary>Whether a click adds to what is selected (ctrl or shift held) rather than replacing it.</summary>
    private bool Modifier()
    {
        var keyboard = Engine.Input.Keyboard;
        return fakeModifier
            || keyboard.IsDown(Key.ControlLeft) || keyboard.IsDown(Key.ControlRight)
            || keyboard.IsDown(Key.ShiftLeft) || keyboard.IsDown(Key.ShiftRight);
    }

    /// <summary>
    /// Helper method to have the UI mark out everything that's selected, and open the pages of any tabs the selection
    /// is hidden away on so it can be seen.
    /// </summary>
    private void MarkSelection()
    {
        stage.Highlighted = document.Selected;
        stage.Highlights = [.. document.Selection];

        foreach (var component in document.Selection)
            RevealTabs(component);

        treeDirty = inspectorDirty = true;
    }

    private static void RevealTabs(UIComponent component)
    {
        for (UIComponent? at = component; at?.Parent is { } parent; at = parent)
        {
            if (parent is TabPanel tabs)
                tabs.Reveal(at);
        }
    }

    /// <summary>Selects a component, on top of the rest with ctrl or shift held, on its own without.</summary>
    private void Pick(UIComponent? component)
    {
        if (component is not null && Modifier())
        {
            document.ToggleSelected(component);
            MarkSelection();
            return;
        }

        Select(component);
    }

    /// <summary>
    /// Helper method to work out what a click on a component picks. A component inside a group picks the group (that's
    /// what a group is for); clicking again goes a level in, the way every drawing program does it. Alt goes straight
    /// to what's under the pointer.
    /// </summary>
    private UIComponent? ResolveClick(UIComponent? hit)
    {
        var keyboard = Engine.Input.Keyboard;
        if (hit is null || keyboard.IsDown(Key.AltLeft) || keyboard.IsDown(Key.AltRight))
            return hit;

        // The groups it's in, the outermost first
        var groups = new List<UIComponent>();
        for (UIComponent? at = hit.Parent; at is not null && at != document.Module.Root; at = at.Parent)
        {
            if (at is Group)
                groups.Insert(0, at);
        }

        if (groups.Count == 0)
            return hit;

        // A group that's selected (or has the selection inside of it) is open: the click goes a level in
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            if (document.Selected is { } selected && IsInside(selected, groups[i]))
                return i + 1 < groups.Count ? groups[i + 1] : hit;
        }

        return groups[0];
    }

    private static bool IsInside(UIComponent component, UIComponent container)
    {
        for (UIComponent? at = component; at is not null; at = at.Parent)
        {
            if (at == container)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Helper method to find the tab of a tabs panel under a point of the layout, so a click on one in the canvas opens
    /// its page (the canvas isn't clickable the way a running UI is, it's being edited).
    /// </summary>
    private static (TabPanel Tabs, int Index)? TabUnder(UIComponent? hit, Vector2 inLayout)
    {
        for (UIComponent? at = hit; at is not null; at = at.Parent)
        {
            if (at is TabPanel tabs && tabs.TabAt(inLayout) is >= 0 and var index)
                return (tabs, index);
        }

        return null;
    }

    /* Doing things to all of the selection */

    private void DeleteAll()
    {
        var gone = document.TopSelection();
        if (gone.Count == 0)
            return;

        foreach (var component in gone)
            document.Remove(component);

        Select(null);
        treeDirty = codeDirty = true;
        Say(gone.Count == 1 ? "deleted it" : $"deleted {gone.Count} things");
    }

    private void GroupSelected()
    {
        if (document.Group(document.TopSelection(), out string problem) is not { } group)
        {
            Say(problem, error: true);
            return;
        }

        Select(group);
        treeDirty = codeDirty = true;
        Say($"grouped {group.Children.Count} things as {document.NameOf(group)}, click again to get at what's in it");
    }

    private void UngroupSelected()
    {
        var groups = document.TopSelection().OfType<Group>().ToList();
        if (groups.Count == 0)
        {
            Say("there's no group selected to take apart", error: true);
            return;
        }

        var freed = new List<UIComponent>();
        foreach (var group in groups)
            freed.AddRange(document.Ungroup(group));

        Select(null);
        foreach (var component in freed)
            document.ToggleSelected(component);

        MarkSelection();
        treeDirty = codeDirty = true;
        Say($"took {groups.Count} group{(groups.Count == 1 ? string.Empty : "s")} apart");
    }

    private void DuplicateSelected()
    {
        var originals = document.TopSelection();
        if (originals.Count == 0)
            return;

        var copies = new List<UIComponent>();
        foreach (var original in originals)
        {
            if (document.Duplicate(original, out string problem) is { } copy)
                copies.Add(copy);
            else
                Say($"couldn't copy {document.NameOf(original)}: {problem}", error: true);
        }

        if (copies.Count == 0)
            return;

        Select(null);
        foreach (var copy in copies)
            document.ToggleSelected(copy);

        MarkSelection();
        treeDirty = codeDirty = true;
        Say(copies.Count == 1 ? $"copied it as {document.NameOf(copies[0])}" : $"copied {copies.Count} things");
    }

    private void ReorderSelected(HexDocument.Order order)
    {
        var moving = document.TopSelection();

        // Going to the front the last one goes last, to the back the first one goes first: they keep their order
        if (order is HexDocument.Order.Back or HexDocument.Order.Forward)
            moving.Reverse();

        foreach (var component in moving)
            document.Reorder(component, order);

        treeDirty = codeDirty = true;
    }

    private void SelectAll()
    {
        // Everything next to what's selected, or everything at the top of the layout
        UIComponent container = document.Selected?.Parent ?? document.Module.Root;

        Select(null);
        foreach (var component in container.Children)
        {
            if (document.Walk().Any(pair => pair.Component == component))
                document.ToggleSelected(component);
        }

        MarkSelection();
        Say($"selected {document.Selection.Count} things");
    }

    private void SelectParent()
    {
        if (document.Selected?.Parent is { } parent && parent != document.Module.Root)
            Select(parent);
    }

    private void AddPage()
    {
        if (document.Selected is not TabPanel tabs)
            return;

        if (document.Add("stack", tabs) is { } page)
        {
            tabs.Tabs = [.. tabs.Tabs, $"Page {tabs.Children.Count}"];
            tabs.Selected = tabs.Children.Count - 1;
            Select(page);
            treeDirty = codeDirty = true;
        }
    }

    /* The right click menu */

    private Menu ComponentMenu()
    {
        var shown = new Menu("component");
        Func<bool> any = () => document.Selection.Count > 0;

        shown.Add("Duplicate", DuplicateSelected, "Ctrl+D").IsEnabled = any;
        shown.Add("Delete", DeleteAll, "Del").IsEnabled = any;
        shown.AddSeparator();
        shown.Add("Bring to front", () => ReorderSelected(HexDocument.Order.Front), "Ctrl+]").IsEnabled = any;
        shown.Add("Bring forward", () => ReorderSelected(HexDocument.Order.Forward), "]").IsEnabled = any;
        shown.Add("Send backward", () => ReorderSelected(HexDocument.Order.Backward), "[").IsEnabled = any;
        shown.Add("Send to back", () => ReorderSelected(HexDocument.Order.Back), "Ctrl+[").IsEnabled = any;
        shown.AddSeparator();
        shown.Add("Group", GroupSelected, "Ctrl+G").IsEnabled = any;
        shown.Add("Ungroup", UngroupSelected, "Ctrl+Shift+G").IsEnabled = () => document.Selection.Any(component => component is Group);
        shown.AddSeparator();
        shown.Add("Select parent", SelectParent).IsEnabled = () => document.Selected?.Parent is { } parent && parent != document.Module.Root;
        shown.Add("Select all", SelectAll, "Ctrl+A");

        if (document.Selected is TabPanel)
            shown.Add("Add a page", AddPage);

        return shown;
    }

    /// <summary>Helper method to show the menu of what's selected where the pointer is, in the editor's own UI.</summary>
    private void ShowComponentMenu(Vector2 world) => contextMenu = ContextMenu.ShowAt(chrome, world, ComponentMenu());

    /// <summary>
    /// Helper method for a right click in the editor that nothing in its UI wanted: on the canvas it picks what's under the
    /// pointer (unless that's selected already, so a menu can be had for all of the selection) and shows the menu.
    /// </summary>
    private void OnContextRequested(UIComponent? over, Vector2 world)
    {
        if (!canvas.Bounds.Contains(chrome.ToLocal(world)))
            return;

        var hit = ResolveClick(document.OwnerOf(document.Module.FindAt(world)));
        if (hit is null || !document.IsSelected(hit))
            Pick(hit);

        ShowComponentMenu(world);
    }

    private void BuildSelectionShortcuts()
    {
        shortcuts.Add(Key.G, GroupSelected, control: true, shift: false);
        shortcuts.Add(Key.G, UngroupSelected, control: true, shift: true);
        shortcuts.Add(Key.D, DuplicateSelected, control: true);
        shortcuts.Add(Key.A, SelectAll, control: true);
        shortcuts.Add(Key.Escape, () => Select(null));
        shortcuts.Add(Key.RightBracket, () => ReorderSelected(HexDocument.Order.Front), control: true);
        shortcuts.Add(Key.LeftBracket, () => ReorderSelected(HexDocument.Order.Back), control: true);
        shortcuts.Add(Key.RightBracket, () => ReorderSelected(HexDocument.Order.Forward));
        shortcuts.Add(Key.LeftBracket, () => ReorderSelected(HexDocument.Order.Backward));

        shortcuts.Add(Key.Number0, ZoomToFit, control: true);
        shortcuts.Add(Key.Number1, ZoomToActualSize, control: true);
        shortcuts.Add(Key.Equal, () => ZoomCanvas(ZOOM_STEP * ZOOM_STEP, canvas.Bounds.Center), control: true);
        shortcuts.Add(Key.Minus, () => ZoomCanvas(1.0f / (ZOOM_STEP * ZOOM_STEP), canvas.Bounds.Center), control: true);
    }
}
