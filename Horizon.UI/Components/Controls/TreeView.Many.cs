using Horizon.Engine;

namespace Horizon.UI.Components;

// Several rows selected at once, Ctrl and a click adding a row or taking it out again and Shift and a click every row
// from the selected one to it, the way a file explorer has them. Only on a tree that says it wants it, and a plain click
// is one row again. The rows picked this way are told about together, nothing is opened by them.
public partial class TreeView
{
    // The rows selected besides the selected one, in the order they were picked
    private readonly List<UITreeItem> alsoSelected = [];

    /// <summary>Whether Ctrl and Shift with a click select more than one row.</summary>
    public bool MultiSelect { get; set; }

    /// <summary>Every row that's selected, the selected one first, none for none.</summary>
    public IReadOnlyList<UITreeItem> SelectedItems => selected is null ? [] : [selected, .. alsoSelected.Where(item => item != selected)];

    /// <summary>More than one row was selected with Ctrl or Shift, or some taken out again, every one that's selected now.</summary>
    public Action<IReadOnlyList<UITreeItem>>? OnSelectionChanged { get; set; }

    /// <summary>Whether a row is one of the selected ones.</summary>
    public bool IsSelected(UITreeItem item) => item == selected || alsoSelected.Contains(item);

    /// <summary>Rows selected together as a click with Ctrl on each would, the first of them the selected one. For a program or a test.</summary>
    public void SelectMany(IReadOnlyList<UITreeItem> items)
    {
        alsoSelected.Clear();
        selected = items.Count > 0 ? items[0] : null;
        alsoSelected.AddRange(items.Skip(1));
        OnSelectionChanged?.Invoke(SelectedItems);
    }

    // A click with Ctrl or Shift on a tree that takes several, true when it was one and has been dealt with
    private bool PickMany(UITreeItem at)
    {
        if (!MultiSelect || GameEngine.Instance?.Input.Keyboard is not { } keyboard || !(keyboard.Control || keyboard.Shift) || selected is null)
        {
            alsoSelected.Clear();
            return false;
        }

        if (keyboard.Shift)
        {
            // Every row showing from the selected one to this one, the selected one staying where the run started
            int from = IndexOf(selected), to = IndexOf(at);
            if (from < 0 || to < 0)
                return false;

            alsoSelected.Clear();
            for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            {
                if (rows[i].Item != selected)
                    alsoSelected.Add(rows[i].Item);
            }
        }
        else if (at == selected)
        {
            // The selected one taken out, the next picked takes its place
            if (alsoSelected.Count == 0)
                return true;
            selected = alsoSelected[0];
            alsoSelected.RemoveAt(0);
        }
        else if (!alsoSelected.Remove(at))
        {
            alsoSelected.Add(at);
        }

        OnSelectionChanged?.Invoke(SelectedItems);
        return true;
    }

    // The rows that went out of sight (a folder closed over them) aren't selected any more
    private void ForgetHidden() => alsoSelected.RemoveAll(item => IndexOf(item) < 0);
}
