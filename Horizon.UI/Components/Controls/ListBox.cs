using Horizon.Content;
using Horizon.Rendering;
using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A list of texts in a box, one of them chosen. Clicking a row chooses it, the wheel scrolls a list longer than
/// the box, and a navigator on the list walks its rows with up and down before it moves on to anything else.
/// Confirming (a click on the chosen row, enter, the gamepad's button) is <see cref="OnActivated"/>, for a list
/// of save games or stages where picking one is the point. For a few options a <see cref="Dropdown"/> or a
/// <see cref="Selector"/> takes less room.
/// <code>
/// let stages = compositor.list({ items: ["Dojo", "Harbour", "Rooftop"], rows: 5, on_changed: name => { ... } });
/// </code>
/// </summary>
public partial class ListBox : UIComponent
{
    private const float DEFAULT_WIDTH = 260.0f;
    private const float ROW_PADDING = 8.0f;
    private const float BAR_WIDTH = UIScrollBar.WIDTH;
    private const int DEFAULT_ROWS = 6;

    private IRuntimeValue? changedHandler, activatedHandler;
    private string[] items = [];
    private int index, top;

    // The first row as it's drawn, gliding to top a fraction of a row at a time. The list jumped a whole row a press or
    // a notch of the wheel, and the rows' letters smeared across the jump between ticks
    private float shown;

    /// <summary>The first row as it's drawn this moment, part way to <see cref="Top"/> while the rows glide.</summary>
    internal float Shown => shown;

    // How many rows a notch of the wheel goes, the rows glide there as quickly as everything else scrolls (UIScrollEase)
    private const int WHEEL_ROWS = 3;

    /// <summary>What there is to choose from, in the order it is shown.</summary>
    public string[] Items
    {
        get => items;
        set
        {
            items = value ?? [];
            matched.Clear();
            index =Math.Clamp(index, 0, Math.Max(0, items.Length - 1));
            top = Math.Clamp(top, 0, Math.Max(0, items.Length - Rows));

            // Other items are other rows, they're where they are at once rather than gliding from the last ones' place
            shown = top;
        }
    }

    /// <summary>Which of the items is chosen, -1 for none.</summary>
    public int Index
    {
        get => index;
        set
        {
            index = items.Length == 0 ? -1 : Math.Clamp(value, -1, items.Length - 1);
            ShowIndex();
        }
    }

    /// <summary>The chosen item itself, empty if there is none. Setting it to something that isn't an item chooses nothing.</summary>
    public string Value
    {
        get => index >= 0 && index < items.Length ? items[index] : string.Empty;
        set => Index = Array.IndexOf(items, value);
    }

    /// <summary>How many rows the box shows at once. Taller than that it scrolls.</summary>
    public int Rows { get; set; } = DEFAULT_ROWS;

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>
    /// How many of the first items are a group of their own over the rest, the commands used last at the top of a
    /// palette, the files opened last at the top of quick open, with a hairline under the last of them. 0 for none.
    /// Their rows were thinner and smaller and tinted, and the first one picked as a palette opened looked shrunk.
    /// </summary>
    public int Lead { get; set; }

    /// <summary>
    /// How an item is shown in three parts, a quiet word in front of it (the menu a command is in), the item itself, and
    /// keys along the right drawn as keycaps ("Ctrl+Shift+P", chords split by a space). Null for every item as it is. The
    /// item itself is still what <see cref="Value"/> and the handlers get.
    /// </summary>
    public Func<string, (string Quiet, string Text, string Keys)>? Columns { get; set; }

    /// <summary>Whether it's only its rows, no box of its own under them and no frame round them, for a list on a card (a palette).</summary>
    public bool Plain { get; set; }

    /// <summary>
    /// A symbol in front of an item, one of <see cref="UISymbols"/>' names (a folder, a kind of file), and the colour it's
    /// drawn in, null for the text's own a little quieter. An empty name keeps its place, so the texts still line up.
    /// Null for a list without symbols.
    /// </summary>
    public Func<string, (string Name, Vector4? Color)>? Icon { get; set; }

    /// <summary>
    /// Quiet columns along the right of a row (a file's size, when it was changed), what each says for an item, each one
    /// right aligned in its width of <see cref="DetailWidths"/>, the last at the right edge. The item's own text is cut
    /// short where they start. Null for none.
    /// </summary>
    public Func<string, string[]>? Details { get; set; }

    /// <summary>How wide each of the <see cref="Details"/> columns is, in the order they are.</summary>
    public float[] DetailWidths { get; set; } = [];

    /// <summary>
    /// What the columns are called, drawn small and quiet in a row of their own over the rows, the item's column first
    /// and then one for each of the <see cref="Details"/>. Empty for no such row.
    /// </summary>
    public string[] Headings { get; set; } = [];

    /// <summary>
    /// A quiet line said in the middle of the room under the rows, while there's room under them ("This folder is
    /// empty"). Empty for nothing.
    /// </summary>
    public string Note { get; set; } = string.Empty;

    // How far a row's symbol is from its text, and how far apart the details are
    private const float ICON_GAP = 8.0f, DETAIL_GAP = 16.0f;

    // How far a row's highlight is in from the sides of the list, and how far apart and how padded the keycaps are
    private const float ROW_INSET = 4.0f, KEY_GAP = 4.0f, KEY_PAD = 6.0f;

    /// <summary>How much of the text's colour is under a picked row, in a list, a tree, a table or a menu, the one pick there is.</summary>
    public const float PICKED = 0.17f;

    /// <summary>Called with the newly chosen item whenever a click or a key changes it.</summary>
    public Action<string>? OnChanged { get; set; }

    /// <summary>Called with the chosen item when it is confirmed, by a click on it or the confirm button of a navigator.</summary>
    public Action<string>? OnActivated { get; set; }

    public ListBox()
    { }

    public ListBox(params string[] items)
    {
        Items = items;
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Navigable => true;

    /// <summary>
    /// Whether Tab stops on it (with a ring while it has the keyboard), up and down and the pages go along it, and Enter
    /// confirms the row. Off unless asked, a list with a box of its own in front (a palette) has that walk it. The file
    /// dialog's places couldn't be got to without the mouse.
    /// </summary>
    public bool TakesKeys { get; set; }

    protected internal override bool TabStop => TakesKeys;

    protected internal override void OnTextInput(char character)
    {
        if (!TakesKeys || items.Length == 0 || !EnabledInHierarchy)
            return;

        int by = character switch
        {
            UIKeyboard.UP => -1,
            UIKeyboard.DOWN => 1,
            UIKeyboard.PAGE_UP => 1 - Rows,
            UIKeyboard.PAGE_DOWN => Rows - 1,
            _ => 0
        };

        if (by != 0)
            Choose(index < 0 ? (by > 0 ? 0 : items.Length - 1) : Math.Clamp(index + by, 0, items.Length - 1));
        else if (character is UIKeyboard.ENTER or ' ')
            OnActivate();
    }

    /// <summary>The first row that is in sight, for a list longer than the box.</summary>
    public int Top => top;

    private float RowHeight(UISkin skin) => MathF.Max(skin.RowHeight, skin.Font.LineHeight * (TextScale > 0.0f ? TextScale : skin.TextScale) + ROW_PADDING);

    /// <summary>
    /// Chooses an item by its index the way a click does, telling whoever listens. Nothing happens for the one chosen already.
    /// </summary>
    public void Choose(int at)
    {
        if (at < 0 || at >= items.Length || at == index)
            return;

        index = at;
        ShowIndex();

        OnChanged?.Invoke(Value);
        InvokeScript(changedHandler, new StringValue(Value));
    }

    /// <summary>
    /// Helper method to scroll so that the chosen row is in sight.
    /// </summary>
    private void ShowIndex()
    {
        if (index < 0)
            return;

        if (index < top)
            top = index;
        else if (index >= top + Rows)
            top = index - Rows + 1;
    }

    protected internal override void OnActivate()
    {
        if (index < 0)
            return;

        OnActivated?.Invoke(Value);
        InvokeScript(activatedHandler, new StringValue(Value));
    }

    protected internal override bool OnNavigate(int right, int down)
    {
        if (down == 0 || items.Length == 0 || !EnabledInHierarchy)
            return false;

        // At either end the press goes on to whatever is past the list
        int next = index + down;
        if (next < 0 || next >= items.Length)
            return false;

        Choose(next);
        return true;
    }

    protected internal override bool OnScroll(float delta)
    {
        if (items.Length <= Rows)
            return false;

        int rows = Math.Max(1, (int)MathF.Round(MathF.Abs(delta) * WHEEL_ROWS));
        top = Math.Clamp(top - rows * Math.Sign(delta), 0, items.Length - Rows);
        return true;
    }

    protected override void Update(float dt)
    {
        base.Update(dt);
        if (shown == top)
            return;

        shown += (top - shown) * (1.0f - MathF.Exp(-UIScrollEase.RATE * dt));
        if (MathF.Abs(top - shown) < 0.01f)
            shown = top;
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (!Bounds.Contains(point) || !EnabledInHierarchy)
            return;

        // The row under the pointer, counting down from the top of the box
        UISkin? skin = Module?.Compositor.Skin;
        if (skin is null)
            return;

        // The row under the pointer, under the headings, where the rows are drawn this moment
        float rowHeight = RowHeight(skin), down = Bounds.Max.Y - HeadingHeight(rowHeight) - point.Y;
        if (down < 0.0f)
            return;
        int row = (int)MathF.Floor(shown + down / rowHeight);
        if (row < 0 || row >= items.Length)
            row = -1;

        if (row < 0)
            return;

        // A click on the chosen row is a confirmation, on another row a choice
        if (row == index)
            OnActivate();
        else
            Choose(row);
    }

    protected override Vector2 Measure(UISkin skin)
    {
        rowHeightSeen = RowHeight(skin);
        return new(DEFAULT_WIDTH, rowHeightSeen * Rows + HeadingHeight(rowHeightSeen));
    }

    // How tall the row of headings is over the rows, a row, so a list of so many rows and its headings ends where a list
    // of one more row beside it does. None without them
    private float HeadingHeight(float rowHeight) => Headings.Length > 0 ? rowHeight : 0.0f;

    // How big a row's symbol is, a line of its text
    private static float IconSide(UISkin skin, float scale) => MathF.Round(skin.Font.LineHeight * scale);

    // how high a row came out the last time it was measured, for a program that fits so many rows to a height
    private float rowHeightSeen;

    /// <summary>How high a row is, as of the last time the list was measured, 0 before it was.</summary>
    public float RowHeightSeen => rowHeightSeen;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
        float rowHeight = RowHeight(skin);

        // A list has no art of its own in any skin, it is a box in the colour of a field unless it is given one
        if (Style.Length > 0 && skin.TryGetRegion(Style, out var art))
            list.NineSlice(art, Bounds, tint);
        else if (!Plain)
            list.Box(Bounds, skin.FieldColor * tint);

        // A list on a card (a palette) keeps the bar's room whether it scrolls or not, so a row's highlight is as wide in
        // a picker of three rows as in one of three hundred
        bool scrolls = items.Length > Rows;
        float right = scrolls || Plain ? Bounds.Max.X - BAR_WIDTH * 2.0f : Bounds.Max.X;
        float headingHeight = HeadingHeight(rowHeight);

        list.PushClip(Bounds);

        // What the columns are, small and quiet over the rows, lined up with what's under them, a hairline under it all
        if (headingHeight > 0.0f)
        {
            list.BeginPart(HEADING_PART);
            UIRect heading = new(new Vector2(Bounds.Min.X, Bounds.Max.Y - headingHeight), new Vector2(right, Bounds.Max.Y));
            UIRect inside = heading.Shrink(new UIEdges(ROW_INSET + ROW_PADDING, 0.0f));
            Vector4 quiet = skin.DimTextColor * tint;
            float small = scale * HEADING_SCALE;
            UIRect first = PaintAround(list, null, inside, small, quiet);
            list.Text(Headings[0], first, Origin.Left, small, quiet, markup: false);
            list.Rect(new UIRect(new Vector2(Bounds.Min.X + ROW_INSET, heading.Min.Y), new Vector2(right - ROW_INSET, heading.Min.Y + 1.0f)), skin.BorderColor.W > 0.0f ? skin.BorderColor * tint : skin.HoverColor);
        }

        // The rows from where the glide has got to, the one half out at the top a fraction of its height over the
        // headings' line and cut off there, and one more under the rest for the bit coming in at the bottom
        int from = Math.Clamp((int)MathF.Floor(shown), 0, Math.Max(0, items.Length - 1));
        int last = Math.Min(items.Length, from + Rows + 1);
        float rowTop = Bounds.Max.Y - headingHeight + (shown - from) * rowHeight;
        list.PushClip(new UIRect(Bounds.Min, new Vector2(Bounds.Max.X, Bounds.Max.Y - headingHeight)));

        // Gliding, a frame between two ticks shows the rows where the newer tick has them. Blended, a row coming in at
        // the bottom had no place the tick before and sat where it was while the one over it slid down onto it
        bool gliding = shown != top;
        if (gliding)
            list.BeginSteps();
        for (int i = from; i < last; i++)
        {
            // By the item, not by where in the box it is, or every scroll had the letters of one item blended with another's
            list.BeginPart(i);
            bool lead = i < Lead;
            float height = rowHeight;
            UIRect row = new(new Vector2(Bounds.Min.X, rowTop - height), new Vector2(right, rowTop));
            rowTop -= height;

            // Always a highlight, see-through when the row isn't lit, so a row has the same quads tick after tick (see TreeView).
            // The chosen one is a rounded slip of the text's colour, the one material every list, tree and menu picks with.
            // It was the accent half seen through, which over Horizon's blue came out the colour of a swamp, and then the
            // accent whole, a bar of gold down every list, and the accent is for what's on and what has the keyboard
            bool chosen = i == index;
            UIRect slip = row.Shrink(new UIEdges(ROW_INSET, 1.0f));
            list.Box(slip, chosen ? skin.TextColor with { W = PICKED } * tint
                : IsHovered && enabled && Hovering(row) ? skin.HoverColor with { W = skin.HoverColor.W * 0.6f } : skin.HoverColor with { W = 0.0f });

            Vector4 ink = skin.TextColor;
            float size = scale;
            UIRect inside = PaintAround(list, items[i], slip.Shrink(new UIEdges(ROW_PADDING, 0.0f)), size, ink * tint);
            if (Choice?.Invoke(items[i]) is { } choice)
                PaintChoice(list, items[i], choice, inside, size, ink * tint, chosen);
            else if (Columns is { } columns)
                PaintColumns(list, columns(items[i]), inside, size, ink * tint, chosen);
            else if (Details is not null)
            {
                // Cut short where the details start, not written under them
                list.PushClip(inside);
                list.Text(Resolve(items[i]), inside, Origin.Left, size, ink * tint, markup: false);
                list.PopClip();
            }
            else
                list.Text(Resolve(items[i]), inside, Origin.Left, size, ink * tint, markup: false);

            // A hairline under the last of the lead, between it and the rest
            if (lead && i == Lead - 1 && i + 1 < items.Length)
                list.Rect(new UIRect(new Vector2(row.Min.X + ROW_PADDING, row.Min.Y), new Vector2(row.Max.X - ROW_PADDING, row.Min.Y + 1.0f)), skin.BorderColor * tint);
        }

        if (gliding)
            list.EndSteps();
        list.PopClip();

        // What it says under the rows while there's room for it, in the middle of that room
        if (Note.Length > 0 && rowTop - Bounds.Min.Y >= rowHeight)
        {
            list.BeginPart(NOTE_PART);
            list.Text(Note, new UIRect(Bounds.Min, new Vector2(right, rowTop)), Origin.Center, scale, skin.DimTextColor * tint, markup: false);
        }

        list.EndParts();

        list.PopClip();

        // A thin bar along the right says how much of the list is in sight, and where, under the headings
        if (scrolls)
        {
            float height = Bounds.Height - headingHeight;
            float share = Rows / (float)items.Length;
            float travel = height * (1.0f - share);
            float barTop = Bounds.Max.Y - headingHeight - travel * (shown / (items.Length - Rows));
            UIScrollBar.Paint(list, Bounds.Max.X - BAR_WIDTH * 0.5f, Bounds.Min.Y, Bounds.Max.Y - headingHeight, barTop, height * share, BAR_WIDTH, 0.0f, false, skin.TextColor);
        }

        if (skin.BorderColor.W > 0.0f && !Plain)
            list.Frame(Bounds, 1.0f, skin.BorderColor * tint);

        PaintSelection(list);
    }

    // The parts the headings and the note are painted as, apart from the rows' own (their items' numbers)
    private const int HEADING_PART = -2, NOTE_PART = -3;

    // How big the headings are written, as a share of the rows' text
    private const float HEADING_SCALE = 0.85f;

    /// <summary>
    /// Helper method to paint what goes round an item's text, its symbol in front and its details along the right (or the
    /// headings of those, for no item), giving back the room left between them for the text.
    /// </summary>
    private UIRect PaintAround(UIDrawList list, string? item, UIRect area, float scale, Vector4 ink)
    {
        UISkin skin = list.Skin;
        float left = area.Min.X, right = area.Max.X;

        if (Icon is { } icon)
        {
            float side = IconSide(skin, scale);
            if (item is not null && icon(item) is { Name.Length: > 0 } symbol)
            {
                var square = new UIRect(new Vector2(left, area.Center.Y - side * 0.5f), new Vector2(left + side, area.Center.Y + side * 0.5f));
                list.Symbol(symbol.Name, square, symbol.Color is { } color ? color with { W = color.W * ink.W } : ink with { W = ink.W * 0.7f });
            }

            left += side + ICON_GAP;
        }

        if (Details is { } details && DetailWidths.Length > 0)
        {
            ReadOnlySpan<string> said = item is null ? Headings.AsSpan(Math.Min(1, Headings.Length)) : details(item);
            Vector4 quiet = item is null ? ink : ink with { W = ink.W * 0.6f };
            for (int c = DetailWidths.Length - 1; c >= 0; c--)
            {
                var column = new UIRect(new Vector2(right - DetailWidths[c], area.Min.Y), new Vector2(right, area.Max.Y));
                if (c < said.Length && said[c].Length > 0)
                    list.Text(said[c], column, Origin.Right, scale, quiet, markup: false);
                right = column.Min.X - DETAIL_GAP;
            }
        }

        return new UIRect(new Vector2(left, area.Min.Y), new Vector2(MathF.Max(left, right), area.Max.Y));
    }

    /// <summary>
    /// A row in its three parts, the quiet word and the item from the left and the keys as keycaps from the right. The item
    /// is cut short where the keys start rather than written under them.
    /// </summary>
    private static void PaintColumns(UIDrawList list, (string Quiet, string Text, string Keys) row, UIRect area, float scale, Vector4 ink, bool chosen)
    {
        UISkin skin = list.Skin;
        Vector4 quiet = ink with { W = ink.W * 0.6f };
        float right = PaintKeys(list, row.Keys, area, scale, ink, chosen);

        list.PushClip(new UIRect(area.Min, new Vector2(MathF.Max(area.Min.X, right), area.Max.Y)));
        float left = area.Min.X;
        if (row.Quiet.Length > 0)
        {
            list.Text(row.Quiet, new UIRect(new Vector2(left, area.Min.Y), area.Max), Origin.Left, scale, quiet, markup: false);
            left += skin.Font.Measure(row.Quiet, scale, markup: false).X + KEY_PAD;
        }

        // What's after a tab is the item's detail, dim after it, a line's code after its number in go to line. Written as
        // keys it came out a keycap a word
        int tab = row.Text.IndexOf('\t');
        if (tab < 0)
        {
            list.Text(row.Text, new UIRect(new Vector2(left, area.Min.Y), area.Max), Origin.Left, scale, ink, markup: false);
        }
        else
        {
            string text = row.Text[..tab];
            list.Text(text, new UIRect(new Vector2(left, area.Min.Y), area.Max), Origin.Left, scale, ink, markup: false);
            left += skin.Font.Measure(text, scale, markup: false).X + KEY_PAD * 3.0f;
            list.Text(row.Text[(tab + 1)..], new UIRect(new Vector2(left, area.Min.Y), area.Max), Origin.Left, scale, quiet, markup: false);
        }

        list.PopClip();
    }

    private bool Hovering(UIRect row)
    {
        if (Module is not { } module)
            return false;

        return row.Contains(module.ToLocal(module.Compositor.Pointer.Position));
    }

    /// <summary>The name of a skin region to draw behind the rows, nothing for the plain box of a field.</summary>
    public string Style { get; set; } = string.Empty;

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("items", () => items, value => Items = value);
        Expose("index", () => Index, value => Index = (int)value);
        Expose("value", () => Value, value => Value = value);
        Expose("rows", () => Rows, value => Rows = Math.Max(1, (int)value));
        Expose("row_height", () => rowHeightSeen, _ => { });
        Expose("style", () => Style, value => Style = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
        Expose("on_activated", () => activatedHandler ?? new NullValue(), value => activatedHandler = value);
    }
}
