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
public sealed partial class UITreeItem
{
    /// <summary>What the row says, plain text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>A few words after the text, in the dim colour, how many there are of something or what it is.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Whether the detail has icons in it to draw, <c>$(arrow-down)</c> and <c>[icon:name]</c>, off for details that hold a name somebody gave something.</summary>
    public bool DetailMarkup { get; set; }

    /// <summary>The colour of the detail, null for the dim one. A key in a list of keys, which is what the row is about.</summary>
    public Vector4? DetailColor { get; set; }

    /// <summary>
    /// A word or two between the text and the detail, always in the dim colour, when something holds for the row (where a
    /// key in a list of keys goes off). The first thing left out when the row is short. Nothing for none.
    /// </summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>The icon in front of the text, a name of <see cref="UISymbols"/>, nothing for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>The colour of the icon, null for the colour of the text turned down.</summary>
    public Vector4? IconColor { get; set; }

    /// <summary>The colour of the text, null for the skin's.</summary>
    public Vector4? Color { get; set; }

    /// <summary>What it says under the pointer, nothing for nothing.</summary>
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>
    /// A piece of the text lit behind, from which character for how many, what was found on a line of Find in Files.
    /// A length of 0 for none.
    /// </summary>
    public (int Start, int Length) Mark { get; set; }

    /// <summary>
    /// Pieces of the text written in the accent (or <see cref="LitColor"/>), from which character for how many. The
    /// words a filter found, lit the way the settings page lights them. Null for none.
    /// </summary>
    public IReadOnlyList<(int Start, int Length)>? Lit { get; set; }

    /// <summary>The colour of the <see cref="Lit"/> pieces, null for the skin's accent.</summary>
    public Vector4? LitColor { get; set; }

    /// <summary>A line down the row's left edge in this colour, the bar a changed setting has. Null for none.</summary>
    public Vector4? Edge { get; set; }

    // The text cut short with dots to the room it had last, kept until the text or the room changes, and the detail's
    internal string? fittedOf;
    internal float fittedRoom;
    internal string fitted = string.Empty;
    internal string? detailOf;
    internal float detailRoom;
    internal string detailFitted = string.Empty;

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
public partial class TreeView : UIComponent
{
    // How thick the line along the top is while it has the keyboard
    private const float FOCUS_LINE = 2.0f;

    private const float DEFAULT_WIDTH = 260.0f, DEFAULT_ROWS = 10.0f;
    private const float ROW_PADDING = 8.0f, BAR_WIDTH = 5.0f, LEFT_PAD = 6.0f, ICON_GAP = 4.0f, DETAIL_GAP = 10.0f;
    private const float DOUBLE_CLICK = 0.4f;

    // How far in from its top and bottom the rows fade while there are more past that edge, see ScrollPanel.Fade
    private const float FADE = 18.0f;

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
            if (value != selected)
                alsoSelected.Clear();
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
        UIRect area = RowsArea(skin);
        float rowTop = area.Max.Y - IndexOf(item) * line + scroll;
        if (rowTop - line < area.Min.Y - 0.5f || rowTop > area.Max.Y + 0.5f)
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

            // One of several selected keeps them all, the menu is for all of them
            if (!IsSelected(at))
                alsoSelected.Clear();
            else if (at != selected && selected is not null)
                alsoSelected.Add(selected);
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
        ForgetHidden();
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

    private float Travel(UISkin skin) => MathF.Max(0.0f, rows.Count * LineHeight(skin) - RowsArea(skin).Height);

    private UITreeItem? RowAt(Vector2 point)
    {
        if (Module?.Compositor.Skin is not { } skin)
            return null;

        // Under the titles of a table, they aren't a row
        UIRect area = RowsArea(skin);
        if (!area.Contains(point))
            return null;

        int row = (int)((area.Max.Y - point.Y + scroll) / LineHeight(skin));
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
        // A title of a table pressed sorts by its column, the owner does the sorting
        if (PressHeader(point))
            return;

        if (RowAt(point) is not { } at || Module?.Compositor.Skin is not { } skin)
            return;

        // A button on the row does what it does and leaves the row as it was, the way VS Code's stage button does
        if (PressAction(at, point))
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

        if (PickMany(at))
            return;

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
        // The owner has first go at a key, a row of its own can mean something by it
        if (selected is { } picked && OnKey?.Invoke(picked, character) == true)
            return;

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
        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : default;
        hovered = Module is not null && IsHovered ? RowAt(pointer) : null;
        Tooltip = hovered?.Tooltip ?? string.Empty;
        UpdateActions(pointer);
        UpdateHeader(pointer);
    }

    protected override Vector2 Measure(UISkin skin) => new(DEFAULT_WIDTH, LineHeight(skin) * DEFAULT_ROWS);

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float line = LineHeight(skin), scale = ScaleOf(skin);

        // The rows go under the titles of a table, all of it otherwise
        UIRect whole = Bounds, bounds = RowsArea(skin);

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
            // Eased there when it's a row or a few the keys went, gone to at once when it's further than the tree is
            // tall (a file revealed far down). It jumped a row a press, and the rows smeared across the jump
            float top = shown * line, bottom = top + line, from = ease.Goal ?? scroll;
            float? wanted = top < from ? top : bottom > from + bounds.Height ? bottom - bounds.Height : null;
            if (wanted is { } goal && MathF.Abs(goal - scroll) > bounds.Height)
            {
                scroll = goal;
                ease.Stop();
            }
            else if (wanted is { } near)
            {
                ease.To(near, travel);
            }
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
        Vector4 lit = text with { W = ListBox.PICKED };
        bool keyed = IsFocused && TakesKeys;

        if (Color is { } behind)
        {
            if (Radius > 0.0f) list.RoundRect(whole, Radius, behind);
            else list.Box(whole, behind);
        }

        list.PushClip(bounds);
        float fade = MathF.Min(FADE, bounds.Height * 0.25f);
        list.PushFade(bounds, MathF.Min(fade, scroll), MathF.Min(fade, travel - scroll));

        int first = (int)(scroll / line);
        int last = Math.Min(rows.Count - 1, first + (int)(bounds.Height / line) + 1);

        // Easing, the rows as the newer tick has them (see UIScrollEase)
        bool easing = ease.Goal is not null;
        if (easing)
            list.BeginSteps();
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
            UIRect pill = row.Shrink(new UIEdges(4.0f, 1.0f));
            list.Box(pill, IsSelected(item) ? lit : item == hovered ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

            // The picked row of a tree with the keyboard has a ring of what has the keyboard round it, the keys go there.
            // Tab into the keys window's list had nothing on screen change. Always painted, see-through when not wanted
            list.RoundOutline(pill, skin.CornerRadius, 1.0f, skin.Palette.Highlight with { W = item == selected && keyed ? 0.8f : 0.0f });

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

            // A table's row, each text in its column (TreeView.Columns), the buttons over its right end
            if (HasColumns)
            {
                PaintCells(list, item, row, x, scale, text, dim);
                PaintActions(list, item, row, 0.0f, icon, text);
                continue;
            }

            // As much of the text as there's room for with its detail after it, cut short with dots rather than off at the
            // edge of the tree, a long line found by Find in Files ran under the side's edge
            // The text comes first, a detail that won't fit beside all of it gets at most a third of the row, cut short
            // itself. A variable's value was cut off to leave its whole type there, "box = geomet...  geometry.shapes.Rect"
            // The badge has its own room at the right end, and the buttons sit over the end of the text while they show
            float badge = BadgeWidth(item, skin, scale);
            float room = right - x - LEFT_PAD - badge;
            float detailWidth = item.Detail.Length > 0 ? skin.Font.Measure(item.Detail, scale, item.DetailMarkup).X + DETAIL_GAP : 0.0f;
            if (detailWidth > 0.0f && skin.Font.Measure(item.Text, scale, markup: false).X + detailWidth > room)
                detailWidth = MathF.Min(detailWidth, room * DETAIL_MOST);

            // The note goes between them while all of the text fits beside it, and is the first thing given up when it doesn't
            float noteWidth = item.Note.Length > 0 ? skin.Font.Measure(item.Note, scale, markup: false).X + DETAIL_GAP : 0.0f;
            if (noteWidth > 0.0f && skin.Font.Measure(item.Text, scale, markup: false).X + detailWidth + noteWidth > room)
                noteWidth = 0.0f;
            string rowText = Fitted(item, skin, scale, room - detailWidth);
            Vector2 size = skin.Font.Measure(rowText, scale, markup: false);
            float baseline = MathF.Round(row.Center.Y + size.Y * 0.5f);

            // Cut off rather than fitted again, so the row's letters stay the same ones as the pointer comes and goes
            bool actions = ShowsActions(item);
            if (actions)
                list.PushClip(new UIRect(row.Min, new Vector2(ActionsLeft(item, row, badge), row.Max.Y)));

            // What was found lit behind it, the colour of a selection, where it still is after any cut
            if (item.Mark.Length > 0 && item.Mark.Start >= 0 && item.Mark.Start + item.Mark.Length <= rowText.Length)
            {
                float from = x + skin.Font.Measure(rowText.AsSpan(0, item.Mark.Start), scale, markup: false).X;
                float width = skin.Font.Measure(rowText.AsSpan(item.Mark.Start, item.Mark.Length), scale, markup: false).X;
                var found = new UIRect(new Vector2(from - 1.0f, row.Center.Y - size.Y * 0.5f - 1.0f), new Vector2(from + width + 1.0f, row.Center.Y + size.Y * 0.5f + 1.0f));
                list.RoundRect(found, 3.0f, skin.Palette.Selection);
            }

            if (item.Lit is { Count: > 0 } pieces)
                PaintLit(list, rowText, pieces, new Vector2(x, baseline), scale, item.Color ?? text, item.LitColor ?? skin.AccentColor);
            else
                list.Text(rowText, new Vector2(x, baseline), scale, item.Color ?? text, markup: false);

            // A changed one's bar down the left edge, the way a changed setting's row has it
            if (item.Edge is { } edge)
                list.Rect(new UIRect(new Vector2(bounds.Min.X + 1.0f, row.Min.Y + 1.0f), new Vector2(bounds.Min.X + 4.0f, row.Max.Y - 1.0f)), edge);

            if (noteWidth > 0.0f)
            {
                Vector2 note = skin.Font.Measure(item.Note, scale, markup: false);
                list.Text(item.Note, new Vector2(x + size.X + DETAIL_GAP, MathF.Round(row.Center.Y + note.Y * 0.5f)), scale, dim, markup: false);
            }

            if (item.Detail.Length > 0 && FittedDetail(item, skin, scale, right - LEFT_PAD - badge - (x + size.X + noteWidth + DETAIL_GAP)) is { Length: > 0 } said)
            {
                Vector2 detail = skin.Font.Measure(said, scale, item.DetailMarkup);
                list.Text(said, new Vector2(x + size.X + noteWidth + DETAIL_GAP, MathF.Round(row.Center.Y + detail.Y * 0.5f)), scale, item.DetailColor ?? dim, item.DetailMarkup);
            }

            if (actions)
                list.PopClip();

            PaintBadge(list, item, row, scale, dim);
            PaintActions(list, item, row, badge, icon, text);
        }

        if (easing)
            list.EndSteps();
        list.EndParts();
        list.PopFade();
        list.PopClip();

        PaintHeader(list, skin, right, icon, scale);

        // The keyboard's in it, in the colour the keyboard is everywhere, the accent's for what's picked
        if (FocusLine && IsFocused)
            list.Rect(new UIRect(new Vector2(whole.Min.X, whole.Max.Y - 1.0f), whole.Max), skin.Palette.Highlight with { W = 0.55f });

        // A thin bar along the right says how much of the tree is in sight, and where
        if (scrolls)
        {
            float share = bounds.Height / (bounds.Height + travel);
            float barTop = bounds.Max.Y - (bounds.Height * (1.0f - share)) * (scroll / travel);
            float barX = bounds.Max.X - BAR_WIDTH * 1.5f;
            list.Rect(new UIRect(new Vector2(barX, barTop - bounds.Height * share), new Vector2(barX + BAR_WIDTH, barTop)), skin.TextColor with { W = 0.25f });
        }
    }

    // A row's text in runs, the lit pieces in their colour and the rest in the text's. Pieces past where the text was cut
    // short are left out, two that overlap make one run
    private static void PaintLit(UIDrawList list, string rowText, IReadOnlyList<(int Start, int Length)> pieces, Vector2 at, float scale, Vector4 color, Vector4 litColor)
    {
        static bool IsLit(IReadOnlyList<(int Start, int Length)> pieces, int index)
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                if (index >= pieces[i].Start && index < pieces[i].Start + pieces[i].Length)
                    return true;
            }

            return false;
        }

        UISkin skin = list.Skin;
        int start = 0;
        while (start < rowText.Length)
        {
            bool lit = IsLit(pieces, start);
            int end = start + 1;
            while (end < rowText.Length && IsLit(pieces, end) == lit)
                end++;

            float from = start == 0 ? 0.0f : skin.Font.Measure(rowText.AsSpan(0, start), scale, markup: false).X;
            list.Text(rowText.AsSpan(start, end - start), new Vector2(at.X + from, at.Y), scale, lit ? litColor : color, markup: false);
            start = end;
        }
    }

    // The dots a row cut short ends with
    private const string CUT = "...";

    // The most of a row a detail gets when the row's too short for all of both
    private const float DETAIL_MOST = 0.34f;

    // A row's detail cut to its room with dots, kept like its text is, nothing when not even the dots fit
    private static string FittedDetail(UITreeItem item, UISkin skin, float scale, float room)
    {
        if (ReferenceEquals(item.detailOf, item.Detail) && item.detailRoom == room)
            return item.detailFitted;

        // Icons in it are drawn whole or not at all, one too long for its room is cut short without them rather than in the
        // middle of a tag
        string shown = item.Detail;
        if (skin.Font.Measure(shown, scale, item.DetailMarkup).X > room)
            shown = skin.Font.Measure(CUT, scale, markup: false).X * 3.0f > room ? string.Empty : Cut(item.DetailMarkup ? WithoutIcons(shown) : shown, skin, scale, room);

        (item.detailOf, item.detailRoom, item.detailFitted) = (item.Detail, room, shown);
        return shown;
    }

    // Some text with its icons taken out, $(name) and [icon:name] both
    private static string WithoutIcons(string text)
    {
        var plain = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '[' && text.AsSpan(i).StartsWith("[icon:") && text.IndexOf(']', i) is var end and > 0)
            {
                i = end;
                continue;
            }

            if (text[i] == '$' && text.AsSpan(i).StartsWith("$(") && text.IndexOf(')', i) is var close and > 0)
            {
                i = close;
                continue;
            }

            plain.Append(text[i]);
        }

        return plain.ToString();
    }

    // As much of some text as fits with dots after it
    private static string Cut(string text, UISkin skin, float scale, float room)
    {
        float dots = skin.Font.Measure(CUT, scale, markup: false).X;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (skin.Font.Measure(text.AsSpan(0, middle), scale, markup: false).X + dots <= room)
                low = middle;
            else
                high = middle - 1;
        }

        return text[..low].TrimEnd() + CUT;
    }

    /// <summary>
    /// Helper method for a row's text as much as fits in so much room, cut with dots at the end, worked out once for the
    /// text and the room and kept on the row.
    /// </summary>
    private static string Fitted(UITreeItem item, UISkin skin, float scale, float room)
    {
        string text = item.Text;
        if (ReferenceEquals(item.fittedOf, text) && item.fittedRoom == room)
            return item.fitted;

        string shown = text;
        if (room > 0.0f && skin.Font.Measure(text, scale, markup: false).X > room)
        {
            float dots = skin.Font.Measure(CUT, scale, markup: false).X;
            int low = 0, high = text.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (skin.Font.Measure(text.AsSpan(0, middle), scale, markup: false).X + dots <= room)
                    low = middle;
                else
                    high = middle - 1;
            }

            shown = text[..low].TrimEnd() + CUT;
        }

        (item.fittedOf, item.fittedRoom, item.fitted) = (text, room, shown);
        return shown;
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
