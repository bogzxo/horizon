using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A column of a <see cref="TreeView"/> that has them, its title in the strip over the rows and how much of the row's
/// width it gets, a share of all the columns' together.
/// </summary>
public sealed record UITreeColumn(string Title, float Width);

public sealed partial class UITreeItem
{
    // The note and the badge cut short with dots to the room their columns had last, the way the text is kept
    internal string? noteOf;
    internal float noteRoom;
    internal string noteFitted = string.Empty;
    internal string? badgeOf;
    internal float badgeRoom;
    internal string badgeFitted = string.Empty;

    /// <summary>
    /// Whether its detail is keys ("Ctrl+K Ctrl+S"), drawn as keycaps in a table's column the way the palette draws them,
    /// not written as words.
    /// </summary>
    public bool DetailKeys { get; set; }
}

// The tree as a table, VS Code's keyboard shortcuts. With columns the rows have a strip of titles over them, pressed to
// sort by one, and a row's text, detail, note and badge each start where their column does, cut short with dots inside
// it. Without them a row is what it always was, the explorer and the search results don't know columns are a thing
public partial class TreeView
{
    // Which title the pointer is on, -1 for none
    private int hoveredHeader = -1;

    /// <summary>
    /// The columns, null for none. With them the text goes in the first, the detail in the second, the note in the third
    /// and the badge in the fourth, each at the left of its column, and the titles go over the rows.
    /// </summary>
    public IReadOnlyList<UITreeColumn>? Columns { get; set; }

    /// <summary>The column the rows are sorted by, which has the arrow in its title, -1 for none. The owner sorts.</summary>
    public int SortColumn { get; set; } = -1;

    /// <summary>Whether the rows are sorted the other way, the arrow pointing down.</summary>
    public bool SortDescending { get; set; }

    /// <summary>A column's title pressed, by its place among the columns. The owner sorts the rows and says so.</summary>
    public Action<int>? OnHeaderPressed { get; set; }

    // Whether it's a table, the strip of titles over the rows
    private bool HasColumns => Columns is { Count: > 0 };

    // How tall the strip of titles is, nothing without columns
    private float HeaderHeight(UISkin skin) => HasColumns ? LineHeight(skin) : 0.0f;

    // Where the rows go, under the strip of titles. All of it without columns
    private UIRect RowsArea(UISkin skin)
    {
        float header = HeaderHeight(skin);
        return header > 0.0f ? new UIRect(Bounds.Min, new Vector2(Bounds.Max.X, MathF.Max(Bounds.Min.Y, Bounds.Max.Y - header))) : Bounds;
    }

    // Where a column starts, its share of the row from the left
    private float ColumnLeft(int column, float left, float right)
    {
        if (Columns is not { Count: > 0 } columns || column <= 0)
            return left;

        float total = 0.0f, before = 0.0f;
        for (int i = 0; i < columns.Count; i++)
        {
            float share = MathF.Max(0.0f, columns[i].Width);
            total += share;
            if (i < column)
                before += share;
        }

        return total > 0.0f ? left + (right - left) * MathF.Min(1.0f, before / total) : left;
    }

    /// <summary>
    /// Where a column's title is on screen right now (like <see cref="RowBounds"/>), null without columns or for one
    /// there isn't. For a test pressing it.
    /// </summary>
    public UIRect? HeaderBounds(int column)
    {
        if (Module?.Compositor.Skin is not { } skin || Columns is not { Count: > 0 } columns || column < 0 || column >= columns.Count)
            return null;

        float right = RightOf(skin), top = Bounds.Max.Y, bottom = top - HeaderHeight(skin);
        float from = ColumnLeft(column, Bounds.Min.X, right), to = column == columns.Count - 1 ? Bounds.Max.X : ColumnLeft(column + 1, Bounds.Min.X, right);
        return new UIRect(new Vector2(from, bottom), new Vector2(to, top));
    }

    /// <summary>
    /// Where a row's text in a column is drawn right now, as far as its letters go, null without columns, for a row out
    /// of sight or a column with nothing in it. For a test that wants each in its column.
    /// </summary>
    public UIRect? CellBounds(UITreeItem item, int column)
    {
        if (Module?.Compositor.Skin is not { } skin || !HasColumns || RowBounds(item) is not { } at || IndexOf(item) is not (>= 0 and var index))
            return null;

        float scale = ScaleOf(skin), line = LineHeight(skin), icon = MathF.Round(line * 0.62f);
        UIRect row = at with { Max = new Vector2(RightOf(skin), at.Max.Y) };
        float x = TextLeft(item, rows[index].Depth, row.Min.X, icon);
        var (shown, from, _) = Cell(item, skin, scale, row, x, column);
        if (shown.Length == 0)
            return null;

        Vector2 size = skin.Font.Measure(shown, scale, markup: false);
        return new UIRect(new Vector2(from, row.Center.Y - size.Y * 0.5f), new Vector2(from + size.X, row.Center.Y + size.Y * 0.5f));
    }

    // Where a row's text starts, past its level, its chevron's room and its icon
    private float TextLeft(UITreeItem item, int depth, float left, float icon) =>
        left + LEFT_PAD + depth * Indent + icon + ICON_GAP + (item.Icon.Length > 0 ? icon + ICON_GAP : 0.0f);

    // What a row says in a column, cut to the column, where it starts and how much room it had
    private (string Shown, float X, float Room) Cell(UITreeItem item, UISkin skin, float scale, UIRect row, float textLeft, int column)
    {
        int count = Columns!.Count;
        float from = column == 0 ? textLeft : ColumnLeft(column, row.Min.X, row.Max.X) + LEFT_PAD;
        float to = column >= count - 1 ? row.Max.X - LEFT_PAD : ColumnLeft(column + 1, row.Min.X, row.Max.X) - LEFT_PAD;
        float room = MathF.Max(0.0f, to - from);
        string shown = column switch
        {
            0 => Fitted(item, skin, scale, room),
            1 => item.Detail.Length > 0 ? FittedDetail(item, skin, scale, room) : string.Empty,
            2 => FitCell(item.Note, ref item.noteOf, ref item.noteRoom, ref item.noteFitted, skin, scale, room),
            3 => FitCell(item.Badge, ref item.badgeOf, ref item.badgeRoom, ref item.badgeFitted, skin, scale, room),
            _ => string.Empty
        };

        // Nothing at all in a column with no room, Fitted leaves the whole text there
        return (room > 0.0f ? shown : string.Empty, from, room);
    }

    // Some text cut to its room with dots, kept until the text or the room changes, nothing when not even the dots fit
    private static string FitCell(string text, ref string? of, ref float at, ref string fitted, UISkin skin, float scale, float room)
    {
        if (ReferenceEquals(of, text) && at == room)
            return fitted;

        string shown = text;
        if (text.Length > 0 && skin.Font.Measure(text, scale, markup: false).X > room)
            shown = skin.Font.Measure(CUT, scale, markup: false).X * 2.0f > room ? string.Empty : Cut(text, skin, scale, room);

        (of, at, fitted) = (text, room, shown);
        return shown;
    }

    // A row of a table, each of its texts in its column, and the bar of a changed one down its edge
    private void PaintCells(UIDrawList list, UITreeItem item, UIRect row, float textLeft, float scale, Vector4 text, Vector4 dim)
    {
        UISkin skin = list.Skin;
        for (int column = 0; column < Columns!.Count && column < 4; column++)
        {
            var (shown, x, room) = Cell(item, skin, scale, row, textLeft, column);
            if (shown.Length == 0)
                continue;

            if (column == 1 && item.DetailKeys)
            {
                UIKeycaps.PaintLeft(list, item.Detail, new UIRect(new Vector2(x, row.Min.Y), new Vector2(x + room, row.Max.Y)), scale, item.DetailColor ?? text, false);
                continue;
            }

            Vector2 size = skin.Font.Measure(shown, scale, markup: false);
            var at = new Vector2(x, MathF.Round(row.Center.Y + size.Y * 0.5f));
            if (column == 0 && item.Lit is { Count: > 0 } pieces)
                PaintLit(list, shown, pieces, at, scale, item.Color ?? text, item.LitColor ?? skin.AccentColor);
            else
                list.Text(shown, at, scale, column switch { 0 => item.Color ?? text, 1 => item.DetailColor ?? dim, 3 => item.BadgeColor ?? dim, _ => dim }, markup: false);
        }

        if (item.Edge is { } edge)
            list.Rect(new UIRect(new Vector2(Bounds.Min.X + 1.0f, row.Min.Y + 1.0f), new Vector2(Bounds.Min.X + 4.0f, row.Max.Y - 1.0f)), edge);
    }

    // The strip of titles over the rows, the one sorted by with its arrow and the one under the pointer lit
    private void PaintHeader(UIDrawList list, UISkin skin, float right, float icon, float scale)
    {
        if (Columns is not { Count: > 0 } columns)
            return;

        Vector4 text = skin.TextColor;
        Vector4 dim = text with { W = text.W * 0.7f };
        Vector4 rule = text with { W = text.W * 0.12f };
        float line = HeaderHeight(skin);
        UIRect strip = new(new Vector2(Bounds.Min.X, Bounds.Max.Y - line), Bounds.Max);

        // Always the same quads, see-through where nothing's lit, the way the rows have their highlight
        for (int column = 0; column < columns.Count; column++)
        {
            float from = ColumnLeft(column, Bounds.Min.X, right), to = column == columns.Count - 1 ? Bounds.Max.X : ColumnLeft(column + 1, Bounds.Min.X, right);
            UIRect cell = new(new Vector2(from, strip.Min.Y), new Vector2(to, strip.Max.Y));
            list.Box(cell.Shrink(new UIEdges(2.0f, 1.0f)), skin.HoverColor with { W = column == hoveredHeader ? skin.HoverColor.W : 0.0f });

            // The first title over the rows' text, past where their chevrons and icons go
            float x = column == 0 ? from + LEFT_PAD + (icon + ICON_GAP) * 2.0f : from + LEFT_PAD;
            bool sorted = column == SortColumn;
            float room = to - x - LEFT_PAD - (sorted ? icon + ICON_GAP : 0.0f);
            string title = columns[column].Title;
            if (room > 0.0f && skin.Font.Measure(title, scale, markup: false).X > room)
                title = Cut(title, skin, scale, room);

            Vector2 size = skin.Font.Measure(title, scale, markup: false);
            if (room > 0.0f)
                list.Text(title, new Vector2(x, MathF.Round(cell.Center.Y + size.Y * 0.5f)), scale, sorted ? text : dim, markup: false);

            if (sorted)
            {
                float arrow = MathF.Round(icon * 0.85f), left = MathF.Min(x + size.X + ICON_GAP, to - LEFT_PAD - arrow);
                list.Symbol(SortDescending ? "arrow-down" : "arrow-up", new UIRect(new Vector2(left, cell.Center.Y - arrow * 0.5f), new Vector2(left + arrow, cell.Center.Y + arrow * 0.5f)), dim);
            }

            if (column > 0)
                list.Rect(new UIRect(new Vector2(from, cell.Min.Y + 4.0f), new Vector2(from + 1.0f, cell.Max.Y - 4.0f)), rule);
        }

        list.Rect(new UIRect(new Vector2(Bounds.Min.X, strip.Min.Y), new Vector2(Bounds.Max.X, strip.Min.Y + 1.0f)), rule);
    }

    // Which title a point is on, -1 for none
    private int HeaderAt(Vector2 point)
    {
        if (Columns is not { Count: > 0 } columns)
            return -1;

        for (int column = 0; column < columns.Count; column++)
        {
            if (HeaderBounds(column) is { } cell && cell.Contains(point))
                return column;
        }

        return -1;
    }

    // A title pressed, true when the press was on one
    private bool PressHeader(Vector2 point)
    {
        if (HeaderAt(point) is not (>= 0 and var column))
            return false;

        OnHeaderPressed?.Invoke(column);
        return true;
    }

    /// <summary>A column's title pressed as if by the pointer, for a test or a program driving it.</summary>
    public void PressHeader(int column)
    {
        if (Columns is { Count: > 0 } columns && column >= 0 && column < columns.Count)
            OnHeaderPressed?.Invoke(column);
    }

    // The title under the pointer, worked out with the row
    private void UpdateHeader(Vector2 pointer) => hoveredHeader = Module is not null && IsHovered ? HeaderAt(pointer) : -1;
}
