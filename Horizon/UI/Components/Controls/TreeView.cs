using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One row of a <see cref="TreeView"/> and the rows under it. What a tree shows is a tree of these, handed over and
/// changed by whoever owns it, then <see cref="TreeView.Refresh"/> to have the tree show the change.
/// </summary>
public sealed class UITreeItem
{
    /// <summary>What the row says, plain text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>A few words after the text, in the dim colour, how many there are of something or what it is.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>The icon in front of the text, a name of <see cref="UISymbols"/>, nothing for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>The colour of the icon, null for the colour of the text turned down.</summary>
    public Vector4? IconColor { get; set; }

    /// <summary>The colour of the text, null for the skin's.</summary>
    public Vector4? Color { get; set; }

    /// <summary>What it says under the pointer, nothing for nothing.</summary>
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>The rows under it, null for none.</summary>
    public List<UITreeItem>? Children { get; set; }

    /// <summary>Whether it has rows under it that aren't made yet, see <see cref="TreeView.OnExpanding"/>. A chevron either way.</summary>
    public bool Expandable { get; set; }

    /// <summary>Whether its rows are showing.</summary>
    public bool Expanded { get; set; }

    /// <summary>Whatever the owner wants to know it by.</summary>
    public object? Tag { get; set; }

    /// <summary>The row it is under, set by the tree as it shows it, null at the top.</summary>
    public UITreeItem? Parent { get; internal set; }

    /// <summary>Whether it opens, it has rows under it or says it will have.</summary>
    public bool HasChildren => Expandable || Children is { Count: > 0 };

    public UITreeItem()
    { }

    public UITreeItem(string text, string icon = "", object? tag = null)
    {
        Text = text;
        Icon = icon;
        Tag = tag;
    }

    /// <summary>Adds a row under this one and hands it back.</summary>
    public UITreeItem Add(UITreeItem child)
    {
        (Children ??= []).Add(child);
        child.Parent = this;
        return child;
    }
}

/// <summary>
/// A tree of rows that open and close, the explorer of an editor, what a file declares, the parts of something. Every
/// row is painted by the tree itself and only the ones in sight are, which is what a list of hundreds of rows wants (a row
/// a component, made out of a template, was a little program a row and the garbage of every rebuild). Chevrons, guides
/// down the levels, an icon each with a colour of its own, the row under the pointer lit and the selected one in the
/// accent, the wheel, and the keys when it has them (up and down, left closes or goes up a level, right opens or goes
/// down one, enter activates). A click selects and, on a row that opens, opens or closes it.
/// <code>
/// let files = compositor.tree_view({ size: vec(260, 400) });
/// </code>
/// The rows are the program's to hand over, <see cref="Items"/>, and to change and <see cref="Refresh"/>.
/// </summary>
public class TreeView : UIComponent
{
    // How thick the line along the top is while it has the keyboard
    private const float FOCUS_LINE = 2.0f;

    private const float DEFAULT_WIDTH = 260.0f, DEFAULT_ROWS = 10.0f;
    private const float ROW_PADDING = 8.0f, BAR_WIDTH = 5.0f, LEFT_PAD = 6.0f, ICON_GAP = 4.0f, DETAIL_GAP = 10.0f;
    private const float DOUBLE_CLICK = 0.4f;

    // The rows that are showing, every item whose parents are all open, in order, with how deep each is
    private readonly List<(UITreeItem Item, int Depth)> rows = [];
    private List<UITreeItem> items = [];
    private UITreeItem? selected, hovered, lastClicked;
    private float scroll, sinceClick = float.MaxValue;
    private UIScrollEase ease;
    private bool revealSelected;

    // The icons the tree draws itself, looked up once
    private char chevronRight, chevronDown;

    /// <summary>The rows at the top of the tree. Changed by the owner, then <see cref="Refresh"/>.</summary>
    public List<UITreeItem> Items
    {
        get => items;
        set
        {
            items = value ?? [];
            Refresh();
        }
    }

    /// <summary>The row that is selected, null for none. Setting it shows it.</summary>
    public UITreeItem? Selected
    {
        get => selected;
        set
        {
            selected = value;
            revealSelected = value is not null;
        }
    }

    /// <summary>The colour of the box behind the rows, null for none.</summary>
    public Vector4? Color { get; set; }

    /// <summary>How round the corners of that box are, 0 for the skin's.</summary>
    public float Radius { get; set; }

    /// <summary>How far in a level is.</summary>
    public float Indent { get; set; } = 14.0f;

    /// <summary>How tall a row is, 0 for as tall as a line of the text and a little.</summary>
    public float RowHeight { get; set; }

    /// <summary>The scale of the text, 0 for the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>Whether lines run down the levels, VS Code's indent guides.</summary>
    public bool Guides { get; set; } = true;

    /// <summary>Whether a click on a row that opens opens it, and not only a click on its chevron.</summary>
    public bool ToggleOnClick { get; set; } = true;

    /// <summary>Whether the tree takes the keyboard when it is clicked. An editor whose shortcuts mustn't be withheld says no.</summary>
    public bool TakesKeys { get; set; } = true;

    /// <summary>
    /// Whether a thin line in the accent runs along its top while it has the keyboard, so which of several things taking
    /// keys has them can be seen (VS Code's focused editor group).
    /// </summary>
    public bool FocusLine { get; set; }

    /// <summary>A click on a row (or the keys selecting one).</summary>
    public Action<UITreeItem>? OnPressed { get; set; }

    /// <summary>A double click on a row, or enter on the selected one.</summary>
    public Action<UITreeItem>? OnActivated { get; set; }

    /// <summary>A row opened or closed.</summary>
    public Action<UITreeItem>? OnToggled { get; set; }

    /// <summary>A row is about to open, the time to make the rows under it if they weren't made (<see cref="UITreeItem.Expandable"/>).</summary>
    public Action<UITreeItem>? OnExpanding { get; set; }

    /// <summary>A right click on a row, with where the pointer is.</summary>
    public Action<UITreeItem, Vector2>? OnItemContextMenu { get; set; }

    /// <summary>
    /// Where a row is on screen right now (in the module's units, like <see cref="UIComponent.Bounds"/>), null if it isn't
    /// showing or is scrolled out of sight. For a test pointing at a row, or something laid over one.
    /// </summary>
    public UIRect? RowBounds(UITreeItem item)
    {
        if (Module?.Compositor.Skin is not { } skin || IndexOf(item) < 0)
            return null;

        float line = LineHeight(skin);
        float rowTop = Bounds.Max.Y - IndexOf(item) * line + scroll;
        if (rowTop - line < Bounds.Min.Y - 0.5f || rowTop > Bounds.Max.Y + 0.5f)
            return null;

        return new UIRect(new Vector2(Bounds.Min.X, rowTop - line), new Vector2(Bounds.Max.X, rowTop));
    }

    /// <summary>Scrolls a row into sight, if it is showing.</summary>
    public void Reveal(UITreeItem item)
    {
        if (IndexOf(item) >= 0)
            revealOnly = item;
    }

    private UITreeItem? revealOnly;

    /// <summary>The rows that are showing, how many.</summary>
    public int VisibleCount => rows.Count;

    /// <summary>The row showing at a place in the list, for a test or a program going through what is on screen.</summary>
    public UITreeItem VisibleAt(int index) => rows[index].Item;

    public TreeView()
    {
        // A right click is for the row under the pointer, which is selected first, the way every tree does it
        OnContextMenu = point =>
        {
            if (RowAt(point) is not { } at)
                return;

            Select(at, notify: false);
            OnItemContextMenu?.Invoke(at, point);
        };
    }

    protected override bool HitTestVisible => true;

    protected internal override bool Focusable => TakesKeys;

    /// <summary>
    /// Shows the rows as they are now, after the owner changed them. Cheap, it goes through the open ones and nothing more.
    /// </summary>
    public void Refresh()
    {
        rows.Clear();
        foreach (UITreeItem item in items)
            Flatten(item, null, 0);

        // The selected row went away, or into a row that closed
        if (selected is not null && rows.FindIndex(row => row.Item == selected) < 0)
            selected = null;
    }

    private void Flatten(UITreeItem item, UITreeItem? parent, int depth)
    {
        item.Parent = parent;
        rows.Add((item, depth));
        if (!item.Expanded || item.Children is null)
            return;

        foreach (UITreeItem child in item.Children)
            Flatten(child, item, depth + 1);
    }

    /// <summary>Opens or closes a row, which the owner hears about.</summary>
    public void Toggle(UITreeItem item)
    {
        if (!item.HasChildren)
            return;

        if (!item.Expanded)
            OnExpanding?.Invoke(item);

        item.Expanded = !item.Expanded;
        Refresh();
        OnToggled?.Invoke(item);
    }

    /// <summary>Opens every row there is, or closes them all.</summary>
    public void ExpandAll(bool open)
    {
        static void Walk(List<UITreeItem>? under, bool open)
        {
            if (under is null) return;
            foreach (UITreeItem item in under)
            {
                if (item.HasChildren) item.Expanded = open;
                Walk(item.Children, open);
            }
        }

        Walk(items, open);
        Refresh();
    }

    private float LineHeight(UISkin skin) => RowHeight > 0.0f ? RowHeight : MathF.Max(skin.RowHeight, MathF.Round(skin.Font.LineHeight * ScaleOf(skin) + ROW_PADDING));

    private float ScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float Travel(UISkin skin) => MathF.Max(0.0f, rows.Count * LineHeight(skin) - Bounds.Height);

    private UITreeItem? RowAt(Vector2 point)
    {
        if (Module?.Compositor.Skin is not { } skin || !Bounds.Contains(point))
            return null;

        int row = (int)((Bounds.Max.Y - point.Y + scroll) / LineHeight(skin));
        return row >= 0 && row < rows.Count ? rows[row].Item : null;
    }

    private int IndexOf(UITreeItem? item)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Item == item)
                return i;
        }

        return -1;
    }

    private void Select(UITreeItem item, bool notify)
    {
        selected = item;
        revealSelected = true;
        if (notify)
            OnPressed?.Invoke(item);
    }

    protected internal override void OnPointerDown(Vector2 point)
    {
        if (RowAt(point) is not { } at || Module?.Compositor.Skin is not { } skin)
            return;

        int depth = rows[IndexOf(at)].Depth;
        float chevron = Bounds.Min.X + LEFT_PAD + depth * Indent;
        bool onChevron = point.X >= chevron - 2.0f && point.X <= chevron + LineHeight(skin) * 0.6f + 2.0f;

        // A second click on the same row soon enough is a double click
        bool twice = at == lastClicked && sinceClick < DOUBLE_CLICK;
        lastClicked = at;
        sinceClick = 0.0f;

        if (twice)
        {
            OnActivated?.Invoke(at);
            return;
        }

        Select(at, notify: true);
        if (at.HasChildren && (onChevron || ToggleOnClick))
            Toggle(at);
    }

    protected internal override bool OnScroll(float delta)
    {
        if (Module?.Compositor.Skin is not { } skin || Travel(skin) <= 0.0f)
            return false;

        ease.Wheel(scroll, -delta * LineHeight(skin) * 3.0f, Travel(skin));
        return true;
    }

    protected internal override void OnTextInput(char character)
    {
        int at = IndexOf(selected);
        switch (character)
        {
            case UIKeyboard.UP when rows.Count > 0:
                Select(rows[Math.Max(0, at - 1)].Item, notify: true);
                break;

            case UIKeyboard.DOWN when rows.Count > 0:
                Select(rows[Math.Min(rows.Count - 1, at + 1)].Item, notify: true);
                break;

            case UIKeyboard.LEFT when selected is { } item:
                if (item.Expanded && item.HasChildren) Toggle(item);
                else if (item.Parent is { } parent) Select(parent, notify: true);
                break;

            case UIKeyboard.RIGHT when selected is { } item:
                if (item.HasChildren && !item.Expanded) Toggle(item);
                else if (item.Expanded && item.Children is [var first, ..]) Select(first, notify: true);
                break;

            case UIKeyboard.ENTER when selected is { } item:
                OnActivated?.Invoke(item);
                break;
        }
    }

    protected override void Update(float dt)
    {
        sinceClick += dt;
        scroll = ease.Step(scroll, dt);

        // The tooltip is the one of the row under the pointer, a string the item has, nothing made
        hovered = Module is { } module && IsHovered ? RowAt(module.ToLocal(module.Compositor.Pointer.Position)) : null;
        Tooltip = hovered?.Tooltip ?? string.Empty;
    }

    protected override Vector2 Measure(UISkin skin) => new(DEFAULT_WIDTH, LineHeight(skin) * DEFAULT_ROWS);

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float line = LineHeight(skin), scale = ScaleOf(skin);
        UIRect bounds = Bounds;

        if (chevronRight == default)
        {
            UISymbols.TryGet("chevron-right", out chevronRight);
            UISymbols.TryGet("chevron-down", out chevronDown);
        }

        // The selected row brought into sight once, after the keys or the owner moved it
        float travel = Travel(skin);
        UITreeItem? reveal = revealOnly ?? (revealSelected ? selected : null);
        revealOnly = null;
        if (IndexOf(reveal) is >= 0 and var shown)
        {
            float top = shown * line, bottom = top + line;
            if (top < scroll) { scroll = top; ease.Stop(); }
            else if (bottom > scroll + bounds.Height) { scroll = bottom - bounds.Height; ease.Stop(); }
        }
        revealSelected = false;
        scroll = Math.Clamp(scroll, 0.0f, travel);
        ease.Clamp(travel);

        bool scrolls = travel > 0.0f;
        float right = scrolls ? bounds.Max.X - BAR_WIDTH * 2.0f : bounds.Max.X;
        float icon = MathF.Round(line * 0.62f);

        Vector4 text = skin.TextColor;
        Vector4 dim = text with { W = text.W * 0.55f };
        Vector4 guide = text with { W = text.W * 0.12f };
        Vector4 lit = skin.AccentColor with { W = 0.28f };

        if (Color is { } behind)
        {
            if (Radius > 0.0f) list.RoundRect(bounds, Radius, behind);
            else list.Box(bounds, behind);
        }

        list.PushClip(bounds);

        int first = (int)(scroll / line);
        int last = Math.Min(rows.Count - 1, first + (int)(bounds.Height / line) + 1);
        for (int i = first; i <= last; i++)
        {
            var (item, depth) = rows[i];
            float rowTop = bounds.Max.Y - i * line + scroll;
            UIRect row = new(new Vector2(bounds.Min.X, rowTop - line), new Vector2(right, rowTop));

            // By the row, not by where on screen it is, or every scroll had the letters of one row blended with another's
            list.BeginPart(i);

            // The highlight is always there, see-through on a row that isn't lit. A row's quads are blended with the same
            // row's of the tick before, and one that came and went with the pointer had the letters blended with the wrong
            // quads and smeared across the tree as the pointer swept over it
            list.Rect(row, item == selected ? lit : item == hovered ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

            // A line down every level this row is inside of, through the middle of where that level's chevron is
            float x = bounds.Min.X + LEFT_PAD;
            if (Guides)
            {
                for (int level = 0; level < depth; level++)
                {
                    float at = MathF.Round(x + level * Indent + icon * 0.5f);
                    list.Rect(new UIRect(new Vector2(at, row.Min.Y), new Vector2(at + 1.0f, row.Max.Y)), guide);
                }
            }

            x += depth * Indent;
            if (item.HasChildren)
                list.Symbol(item.Expanded ? chevronDown : chevronRight, new UIRect(new Vector2(x, row.Center.Y - icon * 0.5f), new Vector2(x + icon, row.Center.Y + icon * 0.5f)), dim);
            x += icon + ICON_GAP;

            if (item.Icon.Length > 0)
            {
                list.Symbol(item.Icon, new UIRect(new Vector2(x, row.Center.Y - icon * 0.5f), new Vector2(x + icon, row.Center.Y + icon * 0.5f)), item.IconColor ?? dim);
                x += icon + ICON_GAP;
            }

            Vector2 size = skin.Font.Measure(item.Text, scale, markup: false);
            list.Text(item.Text, new Vector2(x, MathF.Round(row.Center.Y + size.Y * 0.5f)), scale, item.Color ?? text, markup: false);

            if (item.Detail.Length > 0)
            {
                Vector2 detail = skin.Font.Measure(item.Detail, scale, markup: false);
                list.Text(item.Detail, new Vector2(x + size.X + DETAIL_GAP, MathF.Round(row.Center.Y + detail.Y * 0.5f)), scale, dim, markup: false);
            }
        }

        list.EndParts();
        list.PopClip();

        if (FocusLine && IsFocused)
            list.Rect(new UIRect(new Vector2(bounds.Min.X, bounds.Max.Y - FOCUS_LINE), bounds.Max), skin.AccentColor);

        // A thin bar along the right says how much of the tree is in sight, and where
        if (scrolls)
        {
            float share = bounds.Height / (bounds.Height + travel);
            float barTop = bounds.Max.Y - (bounds.Height * (1.0f - share)) * (scroll / travel);
            float barX = bounds.Max.X - BAR_WIDTH * 1.5f;
            list.Rect(new UIRect(new Vector2(barX, barTop - bounds.Height * share), new Vector2(barX + BAR_WIDTH, barTop)), skin.TextColor with { W = 0.25f });
        }
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("indent", () => Indent, value => Indent = value);
        Expose("color", () => Color ?? Vector4.Zero, value => Color = value);
        Expose("radius", () => Radius, value => Radius = value);
        Expose("row_height", () => RowHeight, value => RowHeight = value);
        Expose("text_scale", () => TextScale, value => TextScale = value);
        Expose("guides", () => Guides, value => Guides = value);
        Expose("toggle_on_click", () => ToggleOnClick, value => ToggleOnClick = value);
        Expose("takes_keys", () => TakesKeys, value => TakesKeys = value);
    }
}
