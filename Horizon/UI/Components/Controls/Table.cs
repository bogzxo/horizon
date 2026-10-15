using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>One column of a <see cref="Table"/>.</summary>
public sealed class UITableColumn
{
    /// <summary>What its header says.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>How wide it is, dragged wider and narrower by the edge of its header.</summary>
    public float Width { get; set; } = 120.0f;

    /// <summary>What its header says under the pointer.</summary>
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>Whatever the owner wants to know it by.</summary>
    public object? Tag { get; set; }

    public UITableColumn()
    { }

    public UITableColumn(string title, float width = 120.0f, object? tag = null)
    {
        Title = title;
        Width = width;
        Tag = tag;
    }
}

/// <summary>One cell of a <see cref="Table"/>.</summary>
public struct UITableCell
{
    /// <summary>What it says, plain text, cut off at the edge of its column.</summary>
    public string Text;

    /// <summary>The colour it is written in, null for the skin's.</summary>
    public Vector4? Color;

    /// <summary>A colour behind it, null for none, a missing text in red say.</summary>
    public Vector4? Fill;

    /// <summary>What it says under the pointer, the whole of a text that doesn't fit.</summary>
    public string Tooltip;

    /// <summary>Whatever the owner wants to know it by.</summary>
    public object? Tag;

    public UITableCell(string text, Vector4? color = null, object? tag = null)
    {
        Text = text;
        Color = color;
        Fill = null;
        Tooltip = text;
        Tag = tag;
    }
}

/// <summary>
/// Rows and columns of text, a header that stays at the top and a first column that stays on the left as the rest
/// scroll, every cell painted by the table itself and only the ones in sight. A click on a header sorts by it (again the
/// other way), its edge drags the column wider, a click selects a cell and a double click (or enter) activates it,
/// which is the owner's to make an edit of. The wheel scrolls down, with shift across.
/// <code>
/// let moves = compositor.table({ size: vec(800, 400) });
/// </code>
/// The columns and rows are the program's to hand over, <see cref="Columns"/> and <see cref="Rows"/>, then
/// <see cref="Refresh"/>.
/// </summary>
public class Table : UIComponent
{
    private const float PAD = 8.0f, GRAB = 5.0f, BAR = 5.0f, MIN_COLUMN = 40.0f, DOUBLE_CLICK = 0.4f;

    // The rows in the order they are shown, by where they are in Rows
    private readonly List<int> order = [];
    private float scrollX, scrollY, sinceClick = float.MaxValue;
    private (int Row, int Column) selected = (-1, -1), hovered = (-1, -1), lastClicked = (-1, -1);
    private int sortColumn = -1;
    private bool sortDown;
    private int resizing = -1;
    private float resizeFrom, resizeWidth;

    /// <summary>The columns, the first of them staying on the left as the rest scroll.</summary>
    public List<UITableColumn> Columns { get; set; } = [];

    /// <summary>The rows, a cell for every column each.</summary>
    public List<UITableCell[]> Rows { get; set; } = [];

    /// <summary>How tall a row is, 0 for a line of the text and a little.</summary>
    public float RowHeight { get; set; }

    /// <summary>The scale of the text, 0 for the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>The colour of the box behind it, null for the skin's field colour.</summary>
    public Vector4? Color { get; set; }

    /// <summary>Whether the first column stays on the left as the rest scroll across.</summary>
    public bool FreezeFirst { get; set; } = true;

    /// <summary>
    /// How far up from its bottom edge the sideways bar runs, for a table with somebody's drag tab along that edge (Hex's
    /// panel under it). 0 for right along the bottom.
    /// </summary>
    public float BarInset { get; set; }

    /// <summary>A click on a cell, its row in <see cref="Rows"/> and its column.</summary>
    public Action<int, int>? OnPressed { get; set; }

    /// <summary>A double click on a cell, or enter on the selected one.</summary>
    public Action<int, int>? OnActivated { get; set; }

    /// <summary>A right click on a cell, and where the pointer is.</summary>
    public Action<int, int, Vector2>? OnCellContextMenu { get; set; }

    /// <summary>The cell that is selected, its row in <see cref="Rows"/> and its column, -1 for none.</summary>
    public (int Row, int Column) Selected
    {
        get => selected;
        set => selected = value;
    }

    public Table()
    {
        OnContextMenu = point =>
        {
            if (CellAt(point) is var (row, column) && row >= 0)
            {
                selected = (row, column);
                OnCellContextMenu?.Invoke(row, column, point);
            }
        };
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Focusable => true;

    /// <summary>
    /// Where a cell is on screen right now (in the module's units, like <see cref="UIComponent.Bounds"/>), null if it is
    /// scrolled out of sight. For an editor laying a box over it, or a test pointing at it.
    /// </summary>
    public UIRect? CellBounds(int row, int column)
    {
        if (Module?.Compositor.Skin is not { } skin || column < 0 || column >= Columns.Count || order.IndexOf(row) < 0)
            return null;

        float line = LineHeight(skin);
        float top = Bounds.Max.Y - line - order.IndexOf(row) * line + scrollY;
        float left = ColumnLeft(column);
        var cell = new UIRect(new Vector2(left, top - line), new Vector2(left + Columns[column].Width, top));

        return cell.Min.Y < Bounds.Min.Y - 0.5f || cell.Max.Y > Bounds.Max.Y - line + 0.5f || cell.Max.X > Bounds.Max.X + 0.5f ? null : cell;
    }

    /// <summary>
    /// Makes every column as wide as what is in it, the header and the first few hundred rows, between a least and a most.
    /// Needs the skin, so after the table is in a module.
    /// </summary>
    public void FitColumns(float least = 60.0f, float most = 320.0f)
    {
        if (Module?.Compositor.Skin is not { } skin)
            return;

        float scale = ScaleOf(skin);
        for (int c = 0; c < Columns.Count; c++)
        {
            float widest = skin.Font.Measure(Columns[c].Title, scale, markup: false).X + PAD * 3.0f;
            for (int r = 0; r < Rows.Count && r < 400; r++)
            {
                if (c < Rows[r].Length && Rows[r][c].Text is { Length: > 0 } text)
                    widest = MathF.Max(widest, skin.Font.Measure(text, scale, markup: false).X + PAD * 2.0f);
            }

            Columns[c].Width = Math.Clamp(MathF.Ceiling(widest), least, most);
        }
    }

    /// <summary>Shows the rows as they are now, sorted the way they were.</summary>
    public void Refresh()
    {
        order.Clear();
        for (int i = 0; i < Rows.Count; i++)
            order.Add(i);

        if (sortColumn >= 0 && sortColumn < Columns.Count)
        {
            int column = sortColumn;
            order.Sort((a, b) =>
            {
                string x = Rows[a].Length > column ? Rows[a][column].Text ?? string.Empty : string.Empty;
                string y = Rows[b].Length > column ? Rows[b][column].Text ?? string.Empty : string.Empty;
                int compared = double.TryParse(x, out double nx) && double.TryParse(y, out double ny) ? nx.CompareTo(ny) : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
                return sortDown ? -compared : compared;
            });
        }

        if (selected.Row >= Rows.Count)
            selected = (-1, -1);
    }

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float LineHeight(UISkin skin) => RowHeight > 0.0f ? RowHeight : MathF.Round(skin.Font.LineHeight * ScaleOf(skin) + PAD);

    private float TotalWidth()
    {
        float total = 0.0f;
        foreach (UITableColumn column in Columns)
            total += column.Width;
        return total;
    }

    private float TravelX => MathF.Max(0.0f, TotalWidth() - Bounds.Width + BAR * 2.0f);

    private float TravelY(UISkin skin) => MathF.Max(0.0f, (order.Count + 1) * LineHeight(skin) - Bounds.Height + BAR * 2.0f);

    /// <summary>Where a column starts across the table, the first at the left edge whatever the scroll when it is frozen.</summary>
    private float ColumnLeft(int column)
    {
        float x = Bounds.Min.X;
        for (int i = 0; i < column; i++)
            x += Columns[i].Width;

        return column == 0 && FreezeFirst ? Bounds.Min.X : x - scrollX;
    }

    private int ColumnAt(float x)
    {
        if (Columns.Count == 0)
            return -1;

        if (FreezeFirst && x < Bounds.Min.X + Columns[0].Width)
            return 0;

        for (int i = FreezeFirst ? 1 : 0; i < Columns.Count; i++)
        {
            float left = ColumnLeft(i);
            if (x >= left && x < left + Columns[i].Width)
                return i;
        }

        return -1;
    }

    /// <summary>The row (in <see cref="Rows"/>) and column under a point, the row -1 on the header, -2 under the rows.</summary>
    private (int Row, int Column)? CellAt(Vector2 point)
    {
        if (Module?.Compositor.Skin is not { } skin || !Bounds.Contains(point))
            return null;

        float line = LineHeight(skin);
        int column = ColumnAt(point.X);
        if (point.Y > Bounds.Max.Y - line)
            return (-1, column);

        int shown = (int)((Bounds.Max.Y - line - point.Y + scrollY) / line);
        return shown >= 0 && shown < order.Count ? (order[shown], column) : (-2, column);
    }

    /// <summary>Which column's right edge is under a point on the header, for dragging it, -1 for none.</summary>
    private int EdgeAt(Vector2 point)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (MathF.Abs(ColumnLeft(i) + Columns[i].Width - point.X) <= GRAB)
                return i;
        }

        return -1;
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        if (CellAt(point) is not var (row, column))
            return;

        // The header, its edge is dragged and the rest of it sorts
        if (row == -1)
        {
            int edge = EdgeAt(point);
            if (edge >= 0)
            {
                resizing = edge;
                resizeFrom = point.X;
                resizeWidth = Columns[edge].Width;
                return;
            }

            if (column >= 0)
            {
                sortDown = sortColumn == column && !sortDown;
                sortColumn = column;
                Refresh();
            }

            return;
        }

        if (row < 0 || column < 0)
            return;

        bool twice = (row, column) == lastClicked && sinceClick < DOUBLE_CLICK;
        lastClicked = (row, column);
        sinceClick = 0.0f;
        selected = (row, column);

        if (twice)
            OnActivated?.Invoke(row, column);
        else
            OnPressed?.Invoke(row, column);
    }

    protected internal override void OnPointerDrag(Vector2 point)
    {
        if (resizing >= 0)
            Columns[resizing].Width = MathF.Max(MIN_COLUMN, resizeWidth + point.X - resizeFrom);
    }

    protected internal override void OnPointerUp(Vector2 point) => resizing = -1;

    protected internal override bool OnScroll(float delta)
    {
        if (Module?.Compositor.Skin is not { } skin)
            return false;

        // Shift turns the wheel across, the way every spreadsheet does
        bool across = Horizon.Engine.GameEngine.Instance?.Input.Keyboard.Shift == true;
        float by = delta * LineHeight(skin) * 3.0f;
        if (across)
            scrollX = Math.Clamp(scrollX - by, 0.0f, TravelX);
        else
            scrollY = Math.Clamp(scrollY - by, 0.0f, TravelY(skin));
        return true;
    }

    protected internal override void OnTextInput(char character)
    {
        int shown = order.IndexOf(selected.Row);
        switch (character)
        {
            case UIKeyboard.UP when shown > 0:
                selected = (order[shown - 1], Math.Max(0, selected.Column));
                break;
            case UIKeyboard.DOWN when shown >= 0 && shown < order.Count - 1:
                selected = (order[shown + 1], Math.Max(0, selected.Column));
                break;
            case UIKeyboard.LEFT when selected.Column > 0:
                selected = (selected.Row, selected.Column - 1);
                break;
            case UIKeyboard.RIGHT when selected.Column >= 0 && selected.Column < Columns.Count - 1:
                selected = (selected.Row, selected.Column + 1);
                break;
            case UIKeyboard.ENTER when selected.Row >= 0 && selected.Column >= 0:
                OnActivated?.Invoke(selected.Row, selected.Column);
                break;
        }
    }

    protected override void Update(float dt)
    {
        sinceClick += dt;

        hovered = Module is { } module && IsHovered && CellAt(module.ToLocal(module.Compositor.Pointer.Position)) is var (row, column) ? (row, column) : (-1, -1);
        Tooltip = hovered.Row >= 0 && hovered.Column >= 0 && hovered.Column < Rows[hovered.Row].Length ? Rows[hovered.Row][hovered.Column].Tooltip ?? string.Empty
            : hovered.Row == -1 && hovered.Column >= 0 ? Columns[hovered.Column].Tooltip : string.Empty;
    }

    protected override Vector2 Measure(UISkin skin) => new(600.0f, LineHeight(skin) * 12.0f);

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        UIRect bounds = Bounds;
        float line = LineHeight(skin), scale = ScaleOf(skin);

        scrollX = Math.Clamp(scrollX, 0.0f, TravelX);
        scrollY = Math.Clamp(scrollY, 0.0f, TravelY(skin));

        list.Box(bounds, Color ?? skin.FieldColor);
        list.PushClip(bounds);

        Vector4 text = skin.TextColor;
        Vector4 rule = text with { W = text.W * 0.1f };
        Vector4 lit = skin.AccentColor with { W = 0.28f };
        Vector4 header = (skin.PanelColor with { W = 1.0f }) * new Vector4(0.85f, 0.85f, 0.85f, 1.0f);

        int first = (int)(scrollY / line);
        int last = Math.Min(order.Count - 1, first + (int)(bounds.Height / line) + 1);

        // The frozen column last, over what scrolls under it
        int part = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int c = 0; c < Columns.Count; c++)
            {
                bool frozen = c == 0 && FreezeFirst;
                if (frozen != (pass == 1))
                    continue;

                float left = ColumnLeft(c), right = left + Columns[c].Width;
                if (right < bounds.Min.X || left > bounds.Max.X)
                    continue;

                for (int i = first; i <= last; i++)
                {
                    int row = order[i];
                    float top = bounds.Max.Y - line - i * line + scrollY;
                    UIRect cell = new(new Vector2(left, top - line), new Vector2(right, top));
                    if (cell.Max.Y < bounds.Min.Y || cell.Min.Y > bounds.Max.Y - line)
                        continue;

                    list.BeginPart(part++);

                    UITableCell value = c < Rows[row].Length ? Rows[row][c] : default;
                    if (frozen)
                        list.Rect(cell, header);
                    if (value.Fill is { } fill)
                        list.Rect(cell, fill);
                    if ((row, c) == selected)
                        list.Rect(cell, lit);
                    else if (row == hovered.Row)
                        list.Rect(cell, skin.HoverColor);

                    if (value.Text is { Length: > 0 } said)
                    {
                        list.PushClip(cell.Shrink(new UIEdges(PAD * 0.5f, 0.0f)));
                        Vector2 size = skin.Font.Measure(said, scale, markup: false);
                        list.Text(said, new Vector2(left + PAD, MathF.Round(cell.Center.Y + size.Y * 0.5f)), scale, value.Color ?? text, markup: false);
                        list.PopClip();
                    }

                    list.Rect(new UIRect(new Vector2(left, cell.Min.Y), new Vector2(right, cell.Min.Y + 1.0f)), rule);
                    list.Rect(new UIRect(new Vector2(right - 1.0f, cell.Min.Y), new Vector2(right, cell.Max.Y)), rule);
                }
            }
        }

        // The header over the rows, sticky, sorted by an arrow
        UIRect head = new(new Vector2(bounds.Min.X, bounds.Max.Y - line), bounds.Max);
        list.BeginPart(part++);
        list.Rect(head, header);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int c = 0; c < Columns.Count; c++)
            {
                bool frozen = c == 0 && FreezeFirst;
                if (frozen != (pass == 1))
                    continue;

                float left = ColumnLeft(c), right = left + Columns[c].Width;
                if (right < bounds.Min.X || left > bounds.Max.X)
                    continue;

                UIRect cell = new(new Vector2(left, head.Min.Y), new Vector2(right, head.Max.Y));
                list.BeginPart(part++);
                if (frozen) list.Rect(cell, header);
                if (hovered == (-1, c)) list.Rect(cell, skin.HoverColor);

                list.PushClip(cell.Shrink(new UIEdges(PAD * 0.5f, 0.0f)));
                Vector2 size = skin.Font.Measure(Columns[c].Title, scale, markup: false);
                list.Text(Columns[c].Title, new Vector2(left + PAD, MathF.Round(cell.Center.Y + size.Y * 0.5f)), scale, text, markup: false);
                list.PopClip();

                if (c == sortColumn)
                {
                    float icon = MathF.Round(line * 0.5f);
                    list.Symbol(sortDown ? "arrow-down" : "arrow-up", new UIRect(new Vector2(right - icon - PAD * 0.5f, cell.Center.Y - icon * 0.5f), new Vector2(right - PAD * 0.5f, cell.Center.Y + icon * 0.5f)), text with { W = 0.6f });
                }

                list.Rect(new UIRect(new Vector2(right - 1.0f, cell.Min.Y), new Vector2(right, cell.Max.Y)), rule);
            }
        }

        list.Rect(new UIRect(new Vector2(bounds.Min.X, head.Min.Y - 1.0f), new Vector2(bounds.Max.X, head.Min.Y)), rule with { W = rule.W * 2.0f });
        list.EndParts();
        list.PopClip();

        // How much is in sight, down the right and along the bottom
        // The text colour faintly, the track and the hover added together came out white in a skin with a dark track
        Vector4 bar = skin.TextColor with { W = 0.25f };
        float travelY = TravelY(skin), travelX = TravelX;
        if (travelY > 0.0f)
        {
            float share = bounds.Height / (bounds.Height + travelY);
            float barTop = bounds.Max.Y - bounds.Height * (1.0f - share) * (scrollY / travelY);
            list.Rect(new UIRect(new Vector2(bounds.Max.X - BAR * 1.5f, barTop - bounds.Height * share), new Vector2(bounds.Max.X - BAR * 0.5f, barTop)), bar);
        }

        if (travelX > 0.0f)
        {
            float share = bounds.Width / (bounds.Width + travelX);
            float barLeft = bounds.Min.X + bounds.Width * (1.0f - share) * (scrollX / travelX);
            float barBottom = bounds.Min.Y + BarInset + BAR * 0.5f;
            list.Rect(new UIRect(new Vector2(barLeft, barBottom), new Vector2(barLeft + bounds.Width * share, barBottom + BAR)), bar);
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("row_height", () => RowHeight, value => RowHeight = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("freeze_first", () => FreezeFirst, value => FreezeFirst = value);
    }
}
