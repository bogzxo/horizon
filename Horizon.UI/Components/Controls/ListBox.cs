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
/// let stages = compositor.list({ items: ["Dojo", "Harbour", "Rooftop"], rows: 5, on_changed: func(name) { ... } });
/// </code>
/// </summary>
public class ListBox : UIComponent
{
    private const float DEFAULT_WIDTH = 260.0f;
    private const float ROW_PADDING = 8.0f;
    private const float BAR_WIDTH = 6.0f;
    private const int DEFAULT_ROWS = 6;

    private IRuntimeValue? changedHandler, activatedHandler;
    private string[] items = [];
    private int index, top;

    /// <summary>What there is to choose from, in the order it is shown.</summary>
    public string[] Items
    {
        get => items;
        set
        {
            items = value ?? [];
            index = Math.Clamp(index, 0, Math.Max(0, items.Length - 1));
            top = Math.Clamp(top, 0, Math.Max(0, items.Length - Rows));
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
    /// palette, the files opened last at the top of quick open. Their rows are thinner and written in a colour of their
    /// own (the text a little towards the accent), with a hairline under the last of them. 0 for none.
    /// </summary>
    public int Lead { get; set; }

    // How tall a row of the lead is, as a share of a row, and how much of the accent its text has
    private const float LEAD_SHARE = 0.78f, LEAD_ACCENT = 0.35f;

    /// <summary>
    /// How an item is shown in three parts, a quiet word in front of it (the menu a command is in), the item itself, and
    /// keys along the right drawn as keycaps ("Ctrl+Shift+P", chords split by a space). Null for every item as it is. The
    /// item itself is still what <see cref="Value"/> and the handlers get.
    /// </summary>
    public Func<string, (string Quiet, string Text, string Keys)>? Columns { get; set; }

    /// <summary>Whether it's only its rows, no box of its own under them and no frame round them, for a list on a card (a palette).</summary>
    public bool Plain { get; set; }

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

    /// <summary>The first row that is in sight, for a list longer than the box.</summary>
    public int Top => top;

    private float RowHeight(UISkin skin) => MathF.Max(skin.RowHeight, skin.Font.LineHeight * (TextScale > 0.0f ? TextScale : skin.TextScale) + ROW_PADDING);

    // How tall an item's row is, a row of the lead thinner
    private float HeightOf(int item, float rowHeight) => item < Lead ? MathF.Round(rowHeight * LEAD_SHARE) : rowHeight;

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

        top = Math.Clamp(top - (int)MathF.Sign(delta), 0, items.Length - Rows);
        return true;
    }

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (!Bounds.Contains(point) || !EnabledInHierarchy)
            return;

        // The row under the pointer, counting down from the top of the box
        UISkin? skin = Module?.Compositor.Skin;
        if (skin is null)
            return;

        // The row under the pointer, the rows of the lead being thinner
        float rowHeight = RowHeight(skin), down = Bounds.Max.Y - point.Y, at = 0.0f;
        int row = -1;
        for (int i = top; i < Math.Min(items.Length, top + Rows); i++)
        {
            at += HeightOf(i, rowHeight);
            if (down < at)
            {
                row = i;
                break;
            }
        }

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
        return new(DEFAULT_WIDTH, rowHeightSeen * Rows);
    }

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

        bool scrolls = items.Length > Rows;
        float right = scrolls ? Bounds.Max.X - BAR_WIDTH * 2.0f : Bounds.Max.X;

        list.PushClip(Bounds);

        int last = Math.Min(items.Length, top + Rows);
        float rowTop = Bounds.Max.Y;
        Vector4 leadText = Vector4.Lerp(skin.TextColor, skin.AccentColor with { W = skin.TextColor.W }, LEAD_ACCENT);
        for (int i = top; i < last; i++)
        {
            // By the item, not by where in the box it is, or every scroll had the letters of one item blended with another's
            list.BeginPart(i);
            bool lead = i < Lead;
            float height = HeightOf(i, rowHeight);
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

            Vector4 ink = lead && !chosen ? leadText : skin.TextColor;
            float size = lead ? scale * 0.9f : scale;
            UIRect inside = slip.Shrink(new UIEdges(ROW_PADDING, 0.0f));
            if (Columns is { } columns)
                PaintColumns(list, columns(items[i]), inside, size, ink * tint, chosen);
            else
                list.Text(Resolve(items[i]), inside, Origin.Left, size, ink * tint, markup: false);

            // A hairline under the last of the lead, between it and the rest
            if (lead && i == Lead - 1 && i + 1 < items.Length)
                list.Rect(new UIRect(new Vector2(row.Min.X + ROW_PADDING, row.Min.Y), new Vector2(row.Max.X - ROW_PADDING, row.Min.Y + 1.0f)), skin.BorderColor * tint);
        }

        list.EndParts();

        list.PopClip();

        // A thin bar along the right says how much of the list is in sight, and where
        if (scrolls)
        {
            float share = Rows / (float)items.Length;
            float travel = Bounds.Height * (1.0f - share);
            float barTop = Bounds.Max.Y - travel * (top / (float)(items.Length - Rows));
            float x = Bounds.Max.X - BAR_WIDTH * 1.5f;

            list.RoundRect(new UIRect(new Vector2(x, barTop - Bounds.Height * share), new Vector2(x + BAR_WIDTH, barTop)), BAR_WIDTH * 0.5f, skin.TextColor with { W = 0.2f });
        }

        if (skin.BorderColor.W > 0.0f && !Plain)
            list.Frame(Bounds, 1.0f, skin.BorderColor * tint);

        PaintSelection(list);
    }

    /// <summary>
    /// A row in its three parts, the quiet word and the item from the left and the keys as keycaps from the right. The item
    /// is cut short where the keys start rather than written under them.
    /// </summary>
    private static void PaintColumns(UIDrawList list, (string Quiet, string Text, string Keys) row, UIRect area, float scale, Vector4 ink, bool chosen)
    {
        UISkin skin = list.Skin;
        Vector4 quiet = ink with { W = ink.W * 0.6f };

        // The keys from the right, each part of a chord a cap of its own
        float right = area.Max.X;
        if (row.Keys.Length > 0)
        {
            float keyScale = scale * 0.82f;
            float height = MathF.Min(area.Height - 6.0f, skin.Font.LineHeight * keyScale + 6.0f);
            string[] caps = row.Keys.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries);
            for (int c = caps.Length - 1; c >= 0; c--)
            {
                float width = MathF.Max(height, skin.Font.Measure(caps[c], keyScale, markup: false).X + KEY_PAD * 2.0f);
                var cap = new UIRect(new Vector2(right - width, area.Center.Y - height * 0.5f), new Vector2(right, area.Center.Y + height * 0.5f));
                list.RoundRect(cap, MathF.Min(skin.CornerRadius, 5.0f), ink with { W = chosen ? 0.16f : 0.08f });
                list.Text(caps[c], cap, Origin.Center, keyScale, quiet, markup: false);
                right = cap.Min.X - KEY_GAP;
            }

            right -= KEY_PAD * 2.0f;
        }

        list.PushClip(new UIRect(area.Min, new Vector2(MathF.Max(area.Min.X, right), area.Max.Y)));
        float left = area.Min.X;
        if (row.Quiet.Length > 0)
        {
            list.Text(row.Quiet, new UIRect(new Vector2(left, area.Min.Y), area.Max), Origin.Left, scale, quiet, markup: false);
            left += skin.Font.Measure(row.Quiet, scale, markup: false).X + KEY_PAD;
        }

        list.Text(row.Text, new UIRect(new Vector2(left, area.Min.Y), area.Max), Origin.Left, scale, ink, markup: false);
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
