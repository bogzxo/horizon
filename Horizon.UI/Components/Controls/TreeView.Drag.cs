using System.Numerics;

using Horizon.Engine;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>Where rows dragged in a tree would go, the row they'd go into and the row they'd land before (null for its end).</summary>
public readonly record struct UITreeDrop(UITreeItem Into, UITreeItem? Before);

// Rows dragged about in the tree, a file into a folder in VS Code's explorer. Once the pointer has gone a few pixels with
// a row held, the rows picked (or the one held) go with it: the row they'd go into is lit with everything showing under
// it, a line says where among its rows they'd land, a tag by the pointer says what's going with a plus while Ctrl copies
// rather than moves, and the tree scrolls by itself with the pointer near its top or bottom. The owner says what can go
// where (DropAt), asked only when the row under the pointer changes, and does it (OnDrop).
public partial class TreeView
{
    /// <summary>
    /// Where the rows dragged would go with the pointer over a row (null for under the rows), null for nowhere. Rows
    /// can only be dragged while this and <see cref="OnDrop"/> are both set.
    /// </summary>
    public Func<IReadOnlyList<UITreeItem>, UITreeItem?, UITreeDrop?>? DropAt { get; set; }

    /// <summary>The rows let go of over somewhere they can go, with where, and whether they're copied rather than moved.</summary>
    public Action<IReadOnlyList<UITreeItem>, UITreeDrop, bool>? OnDrop { get; set; }

    /// <summary>Whether a drop copies right now, null for while Ctrl is held.</summary>
    public Func<bool>? DropCopies { get; set; }

    /// <summary>What the tag by the pointer says for several rows, {0} for how many.</summary>
    public string DragManyText { get; set; } = "{0} items";

    // How far the pointer goes before a press is a drag, and how near the top or bottom it scrolls the tree, in rows
    private const float DRAG_START = 6.0f, EDGE_ROWS = 1.5f, EDGE_SPEED = 14.0f;

    private readonly List<UITreeItem> dragged = [];
    private UITreeItem? heldRow, overRow, pendingToggle;
    private Vector2 heldAt;
    private bool dragging, overAsked;
    private UITreeDrop? drop;
    private int draggedCountSaid = -1;
    private string draggedSaid = string.Empty;

    /// <summary>Whether rows are being dragged right now.</summary>
    public bool Dragging => dragging;

    /// <summary>Where the rows dragged would go now, null for nowhere.</summary>
    public UITreeDrop? DropShown => dragging ? drop : null;

    /// <summary>Whether the drop going on now would copy.</summary>
    public bool DragCopies => dragging && Copies;

    private bool CanDrag => DropAt is not null && OnDrop is not null;

    private bool Copies => DropCopies?.Invoke() ?? GameEngine.Instance?.Input.Keyboard is { Control: true };

    // A press on a row that might turn into a drag
    private void HoldRow(UITreeItem row, Vector2 point)
    {
        heldRow = CanDrag ? row : null;
        heldAt = point;
        dragging = false;
    }

    private void DragRows(Vector2 point)
    {
        if (heldRow is null || draggingBar)
            return;

        if (!dragging)
        {
            if (Vector2.Distance(point, heldAt) < DRAG_START)
                return;

            // The rows picked go when the one held is one of them, otherwise just it
            dragged.Clear();
            if (IsSelected(heldRow))
            {
                if (selected is not null)
                    dragged.Add(selected);
                foreach (UITreeItem also in alsoSelected)
                    dragged.Add(also);
            }
            else
            {
                dragged.Add(heldRow);
            }

            dragging = true;
            pendingToggle = null;
            overAsked = false;
            ClearFind();
        }

        Aim(point);
    }

    // The row under the pointer and where the rows would go, asked again only when that row changes
    private void Aim(Vector2 point)
    {
        UITreeItem? over = RowAt(point);
        if (overAsked && over == overRow)
            return;

        overRow = over;
        overAsked = true;
        drop = DropAt?.Invoke(dragged, over);
    }

    private void DropRows()
    {
        bool was = dragging;
        UITreeDrop? going = drop;
        bool copy = was && Copies;
        dragging = false;
        heldRow = null;
        overRow = null;
        drop = null;
        if (was && going is { } at)
            OnDrop?.Invoke(dragged, at, copy);
        dragged.Clear();
    }

    /// <summary>Lets go of a drag without dropping anything, what Escape does.</summary>
    public void CancelDrag()
    {
        dragging = false;
        heldRow = null;
        overRow = null;
        drop = null;
        dragged.Clear();
    }

    // Every tick of a drag, the tree scrolled by itself with the pointer near an end, faster the nearer, and the row
    // under it looked at again as the rows go by
    private void UpdateDrag(float dt, Vector2 pointer)
    {
        if (!dragging || Module?.Compositor.Skin is not { } skin)
            return;

        UIRect area = RowsArea(skin);
        float line = LineHeight(skin), edge = MathF.Min(line * EDGE_ROWS, area.Height * 0.25f), travel = Travel(skin);
        float by = 0.0f;
        if (pointer.Y > area.Max.Y - edge)
            by = -MathF.Min(1.0f, (pointer.Y - (area.Max.Y - edge)) / edge);
        else if (pointer.Y < area.Min.Y + edge)
            by = MathF.Min(1.0f, (area.Min.Y + edge - pointer.Y) / edge);

        if (by != 0.0f && travel > 0.0f)
        {
            ease.Stop();
            scroll = Math.Clamp(scroll + by * line * EDGE_SPEED * dt, 0.0f, travel);
            revealSelected = false;
            revealOnly = null;
            overAsked = false;
        }

        Aim(pointer);
    }

    // The row they'd go into and what's showing under it lit, painted as part of each row so its quads stay the same
    private bool InDrop(UITreeItem item)
    {
        if (!dragging || drop is not { } at)
            return false;

        for (UITreeItem? up = item; up is not null; up = up.Parent)
        {
            if (up == at.Into)
                return true;
        }

        return false;
    }

    // The line where they'd land and the tag by the pointer, over the rows
    private void PaintDrag(UIDrawList list, UISkin skin, UIRect area, float right, float scale)
    {
        if (!dragging || Module is not { } module)
            return;

        float line = LineHeight(skin);
        if (drop is { } at && at.Into.Expanded && IndexOf(at.Into) is >= 0 and var into)
        {
            // Before a row of it, or after the last row showing under it
            int landing = at.Before is { } before && IndexOf(before) is >= 0 and var beforeAt ? beforeAt : LastUnder(into) + 1;
            float y = area.Max.Y - landing * line + scroll;
            if (y >= area.Min.Y - 1.0f && y <= area.Max.Y + 1.0f)
            {
                float left = area.Min.X + LEFT_PAD + (rows[into].Depth + 1) * Indent;
                list.RoundRect(new UIRect(new Vector2(left, y - 1.0f), new Vector2(right - LEFT_PAD, y + 1.0f)), 1.0f, skin.AccentColor);
                list.RoundRect(new UIRect(new Vector2(left - 3.0f, y - 3.0f), new Vector2(left + 3.0f, y + 3.0f)), 3.0f, skin.AccentColor);
            }
        }

        // The tag, what's going and a plus in front while it's copied
        Vector2 pointer = module.ToLocal(module.Compositor.Pointer.Position);
        UITreeItem first = dragged[0];
        if (dragged.Count != draggedCountSaid)
        {
            draggedCountSaid = dragged.Count;
            draggedSaid = dragged.Count > 1 ? string.Format(System.Globalization.CultureInfo.CurrentCulture, DragManyText, dragged.Count) : string.Empty;
        }

        string said = dragged.Count > 1 ? draggedSaid : first.Text;
        float icon = MathF.Round(line * 0.55f);
        Vector2 size = skin.Font.Measure(said, scale, markup: false);
        bool copy = Copies;
        float width = 8.0f + (copy ? icon + ICON_GAP : 0.0f) + (first.Icon.Length > 0 ? icon + ICON_GAP : 0.0f) + size.X + 10.0f;
        var tag = new UIRect(new Vector2(pointer.X + 14.0f, pointer.Y - 10.0f - line), new Vector2(pointer.X + 14.0f + width, pointer.Y - 10.0f));
        list.RoundRect(tag, skin.CornerRadius, skin.PanelColor with { W = 1.0f });
        list.RoundRect(tag, skin.CornerRadius, skin.HoverColor);
        list.RoundOutline(tag, skin.CornerRadius, 1.0f, drop is null ? skin.BorderColor with { W = MathF.Max(skin.BorderColor.W, 0.3f) } : skin.AccentColor);

        float x = tag.Min.X + 8.0f;
        if (copy)
        {
            list.Symbol("add", new UIRect(new Vector2(x, tag.Center.Y - icon * 0.5f), new Vector2(x + icon, tag.Center.Y + icon * 0.5f)), skin.AccentColor);
            x += icon + ICON_GAP;
        }

        if (first.Icon.Length > 0)
        {
            list.Symbol(dragged.Count > 1 ? "files" : first.Icon, new UIRect(new Vector2(x, tag.Center.Y - icon * 0.5f), new Vector2(x + icon, tag.Center.Y + icon * 0.5f)), first.IconColor ?? skin.TextColor with { W = 0.7f });
            x += icon + ICON_GAP;
        }

        list.Text(said, new UIRect(new Vector2(x, tag.Min.Y), new Vector2(x + size.X, tag.Max.Y)), Origin.Left, scale, skin.TextColor with { W = drop is null ? 0.6f : 1.0f }, markup: false);
    }

    // The last row showing under a row, itself when it has none open
    private int LastUnder(int index)
    {
        int depth = rows[index].Depth, last = index;
        for (int i = index + 1; i < rows.Count && rows[i].Depth > depth; i++)
            last = i;
        return last;
    }
}
