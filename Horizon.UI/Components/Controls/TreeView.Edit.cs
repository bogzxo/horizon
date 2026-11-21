using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// A row's name typed over in place, VS Code's rename in its explorer and the new file or folder that's named where it
// will be. A box over the row's text with part of it picked, what's wrong with what's typed said under the row as it's
// typed, Enter keeps it, Escape leaves it as it was, and the keyboard going somewhere else keeps it if it's all right.
public partial class TreeView
{
    private TextBox? editBox;
    private UITreeItem? edited;
    private Func<string, string?>? editCheck;
    private Func<string, bool>? editCommit;
    private Action? editCancelled;
    private string editProblem = string.Empty;
    private bool editHadKeys;

    // How much room the box has round what's typed, and how far the note under it is from the row
    private const float EDIT_PAD = 4.0f, PROBLEM_PAD = 6.0f;

    /// <summary>The row whose name is being typed over, null for none.</summary>
    public UITreeItem? Edited => edited;

    /// <summary>The box the name is typed into while a row is edited, null otherwise.</summary>
    public TextBox? EditBox => edited is not null ? editBox : null;

    /// <summary>What's wrong with what's typed into the row, empty while it's all right.</summary>
    public string EditProblem => editProblem;

    /// <summary>Where the note under the edited row is, empty while there's none.</summary>
    public UIRect EditProblemBounds { get; private set; }

    /// <summary>
    /// Types over a row's name in place. The row is shown, a box goes over its text with <paramref name="text"/> in it and
    /// the part from <paramref name="pickStart"/> picked, and the keyboard goes there.
    /// </summary>
    /// <param name="check">What's wrong with what's typed, asked as it's typed, null or empty for nothing.</param>
    /// <param name="commit">Enter, or the keyboard going elsewhere with nothing wrong. True when it was taken and the box goes.</param>
    /// <param name="cancelled">Escape, or the keyboard going elsewhere with something wrong.</param>
    public void Edit(UITreeItem item, string text, int pickStart, int pickLength, Func<string, string?>? check, Func<string, bool> commit, Action? cancelled = null)
    {
        if (edited is not null)
            EndEdit(keep: false);
        if (IndexOf(item) < 0)
            return;

        ClearFind();
        edited = item;
        editCheck = check;
        editCommit = commit;
        editCancelled = cancelled;
        editProblem = string.Empty;
        editHadKeys = false;
        selected = item;
        alsoSelected.Clear();
        revealOnly = item;

        editBox ??= MakeEditBox();
        if (editBox.Parent != this)
            Add(editBox);
        editBox.Visible = true;
        editBox.Text = text;
        int start = Math.Clamp(pickStart, 0, text.Length);
        editBox.Select(start, Math.Clamp(start + pickLength, start, text.Length));
        PlaceEditBox();
        editBox.Focus();
    }

    private TextBox MakeEditBox()
    {
        var box = new TextBox { Bare = true, Style = "textbox_tree_edit", MaxLength = 1024, Padding = new UIEdges(EDIT_PAD, 0.0f) };
        box.OnChanged = text => editProblem = editCheck?.Invoke(text) ?? string.Empty;
        box.OnSubmitted = text => TryKeep(text);
        return box;
    }

    // Enter, kept when nothing's wrong with it and the owner took it
    private void TryKeep(string text)
    {
        if (edited is null)
            return;

        editProblem = editCheck?.Invoke(text) ?? string.Empty;
        if (editProblem.Length == 0)
            EndEdit(keep: true);
    }

    /// <summary>Stops typing over the row, what's typed kept or left (the owner told it was left).</summary>
    public void EndEdit(bool keep)
    {
        if (edited is null)
            return;

        if (keep && editBox is not null && editProblem.Length == 0 && edited is not null)
        {
            UITreeItem was = edited;
            string text = editBox.Text;
            Func<string, bool>? commit = editCommit;
            CloseEdit(focusTree: true);
            if (commit?.Invoke(text) == false && edited is null)
                Edit(was, text, 0, text.Length, editCheck, commit, editCancelled);
            return;
        }

        Action? cancelled = editCancelled;
        CloseEdit(focusTree: true);
        cancelled?.Invoke();
    }

    private void CloseEdit(bool focusTree)
    {
        bool hadKeys = editBox?.IsFocused ?? false;
        edited = null;
        editCheck = null;
        editCommit = null;
        editCancelled = null;
        editProblem = string.Empty;
        EditProblemBounds = default;
        if (editBox is not null)
        {
            editBox.Visible = false;
            Remove(editBox);
        }

        if (focusTree && hadKeys && TakesKeys)
            Focus();
    }

    // Where the box goes, over the row's text to the right end, out of sight while the row is scrolled away
    private void PlaceEditBox()
    {
        if (edited is null || editBox is null || Module?.Compositor.Skin is not { } skin)
            return;

        int index = IndexOf(edited);
        if (RowBounds(edited) is not { } row || index < 0)
        {
            editBox.ArrangeTree(new UIRect(new Vector2(-10000.0f), new Vector2(-9999.0f)));
            return;
        }

        float left = TextLeft(skin, index) - EDIT_PAD - 1.0f;
        float right = (Travel(skin) > 0.0f ? RowsArea(skin).Max.X - BAR_WIDTH * 2.0f : RowsArea(skin).Max.X) - LEFT_PAD;
        editBox.Size = new Vector2(MathF.Max(40.0f, right - left), row.Height - 2.0f);
        editBox.TextScale = ScaleOf(skin);
        editBox.ArrangeTree(new UIRect(new Vector2(left, row.Min.Y + 1.0f), new Vector2(MathF.Max(left + 40.0f, right), row.Max.Y - 1.0f)));
    }

    // Where a row's text starts, past its levels, its chevron and its icon
    private float TextLeft(UISkin skin, int index)
    {
        var (item, depth) = rows[index];
        float icon = MathF.Round(LineHeight(skin) * 0.62f);
        float x = Bounds.Min.X + LEFT_PAD + depth * Indent + icon + ICON_GAP;
        return item.Icon.Length > 0 ? x + icon + ICON_GAP : x;
    }

    protected override void Arrange(UIRect content)
    {
        base.Arrange(content);
        PlaceEditBox();
    }

    // Every tick, the box kept on its row as the tree scrolls, and the edit ended once its row has gone (the owner made
    // the rows again) or the keyboard went somewhere else
    private void UpdateEdit()
    {
        if (edited is null || editBox is null)
            return;

        if (IndexOf(edited) < 0)
        {
            Action? cancelled = editCancelled;
            CloseEdit(focusTree: false);
            cancelled?.Invoke();
            return;
        }

        PlaceEditBox();
        if (editBox.IsFocused)
        {
            editHadKeys = true;
        }
        else if (editHadKeys)
        {
            editProblem = editCheck?.Invoke(editBox.Text) ?? string.Empty;
            EndEdit(keep: editProblem.Length == 0);
        }
    }

    // The box's ground and rim under what's typed, the rim in the error colour while something's wrong, and what's wrong
    // said in a note under the row (over it on the last rows)
    private void PaintEdit(UIDrawList list, UISkin skin, UIRect area, float scale)
    {
        if (edited is null || editBox is null || RowBounds(edited) is null)
        {
            EditProblemBounds = default;
            return;
        }

        UIRect box = editBox.Bounds;
        bool wrong = editProblem.Length > 0;
        list.RoundRect(box, skin.CornerRadius, skin.PanelColor with { W = 1.0f });
        list.RoundRect(box, skin.CornerRadius, skin.FieldColor);
        list.RoundOutline(box, skin.CornerRadius, 1.0f, wrong ? skin.ErrorColor : skin.HighlightColor with { W = 0.85f });

        if (!wrong)
        {
            EditProblemBounds = default;
            return;
        }

        float room = MathF.Max(60.0f, box.Width);
        Vector2 said = skin.Font.Measure(editProblem, scale, markup: false);
        string shown = said.X + PROBLEM_PAD * 2.0f > room ? Cut(editProblem, skin, scale, room - PROBLEM_PAD * 2.0f) : editProblem;
        float width = MathF.Min(room, said.X + PROBLEM_PAD * 2.0f), height = said.Y + PROBLEM_PAD * 1.5f;

        // Under the box, Y going up, or over it when that would leave the tree
        UIRect note = new(new Vector2(box.Min.X, box.Min.Y - height + 1.0f), new Vector2(box.Min.X + width, box.Min.Y + 1.0f));
        if (note.Min.Y < area.Min.Y)
            note = new UIRect(new Vector2(box.Min.X, box.Max.Y - 1.0f), new Vector2(box.Min.X + width, box.Max.Y + height - 1.0f));
        EditProblemBounds = note;

        list.RoundRect(note, skin.CornerRadius, skin.PanelColor with { W = 1.0f });
        list.RoundRect(note, skin.CornerRadius, skin.ErrorColor with { W = 0.16f });
        list.RoundOutline(note, skin.CornerRadius, 1.0f, skin.ErrorColor);
        list.Text(shown, note.Shrink(new UIEdges(PROBLEM_PAD, 0.0f)), Origin.Left, scale, skin.TextColor, markup: false);
    }
}
