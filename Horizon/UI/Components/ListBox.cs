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

    private float RowHeight(UISkin skin) => skin.Font.LineHeight * (TextScale > 0.0f ? TextScale : skin.TextScale) + ROW_PADDING;

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

        int row = top + (int)((Bounds.Max.Y - point.Y) / RowHeight(skin));
        if (row < top || row >= Math.Min(items.Length, top + Rows))
            return;

        // A click on the chosen row is a confirmation, on another row a choice
        if (row == index)
            OnActivate();
        else
            Choose(row);
    }

    protected override Vector2 Measure(UISkin skin) => new(DEFAULT_WIDTH, RowHeight(skin) * Rows);

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        bool enabled = EnabledInHierarchy;
        Vector4 tint = enabled ? Vector4.One : skin.DisabledTint;
        float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
        float rowHeight = RowHeight(skin);

        list.Box(Bounds, skin.FieldColor * tint);

        bool scrolls = items.Length > Rows;
        float right = scrolls ? Bounds.Max.X - BAR_WIDTH * 2.0f : Bounds.Max.X;

        list.PushClip(Bounds);

        int last = Math.Min(items.Length, top + Rows);
        for (int i = top; i < last; i++)
        {
            list.BeginPart(i - top);
            float rowTop = Bounds.Max.Y - (i - top) * rowHeight;
            UIRect row = new(new Vector2(Bounds.Min.X, rowTop - rowHeight), new Vector2(right, rowTop));

            if (i == index)
                list.Rect(row, skin.AccentColor * tint * new Vector4(1.0f, 1.0f, 1.0f, 0.55f));
            else if (IsHovered && enabled && Hovering(row))
                list.Rect(row, skin.HoverColor);

            list.Text(items[i], row.Shrink(new UIEdges(ROW_PADDING, 0.0f)), Origin.Left, scale, skin.TextColor * tint, markup: false);
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

            list.Rect(new UIRect(new Vector2(x, barTop - Bounds.Height * share), new Vector2(x + BAR_WIDTH, barTop)), skin.TrackColor + skin.HoverColor);
        }

        if (skin.BorderColor.W > 0.0f)
            list.Frame(Bounds, 1.0f, skin.BorderColor * tint);

        PaintSelection(list);
    }

    private bool Hovering(UIRect row)
    {
        if (Module is not { } module)
            return false;

        return row.Contains(module.ToLocal(module.Compositor.Pointer.Position));
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("items", () => items, value => Items = value);
        Expose("index", () => Index, value => Index = (int)value);
        Expose("value", () => Value, value => Value = value);
        Expose("rows", () => Rows, value => Rows = Math.Max(1, (int)value));
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("on_changed", () => changedHandler ?? new NullValue(), value => changedHandler = value);
        Expose("on_activated", () => activatedHandler ?? new NullValue(), value => activatedHandler = value);
    }
}
