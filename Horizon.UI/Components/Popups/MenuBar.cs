using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One line of a <see cref="Menu"/>, something to do, a rule between two groups of them, or a menu of its own that
/// opens to the side when the pointer rests on it.
/// </summary>
public sealed class MenuItem
{
    /// <summary>What the item says. Empty for a separator.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>What is written at the far end of the item, which is where the keys that do the same thing go.</summary>
    public string Shortcut { get; set; } = string.Empty;

    /// <summary>One of the icons every skin has (<see cref="UISymbols"/>) in front of it. Empty for none, null taken as none.</summary>
    /// <remarks>Null got through from a caller with nullable warnings off and the menu threw drawing it, taking the program with it.</remarks>
    public string Icon { get => icon; set => icon = value ?? string.Empty; }
    private string icon = string.Empty;

    /// <summary>What picking the item does.</summary>
    public Action? OnPicked { get; set; }

    /// <summary>Asked whenever the menu is drawn or clicked. Whether the item can be picked right now. Always, if nothing is set.</summary>
    public Func<bool>? IsEnabled { get; set; }

    /// <summary>Asked whenever the menu is drawn. Whether the item has a tick in front of it, for one that switches something on and off.</summary>
    public Func<bool>? IsChecked { get; set; }

    /// <summary>Asked whenever the menu is drawn. Whether the item is there at all right now. Always, if nothing is set.</summary>
    public Func<bool>? IsVisible { get; set; }

    /// <summary>The menu the item opens to the side, null for an item that does something itself.</summary>
    public Menu? Submenu { get; init; }

    /// <summary>Whether this is a rule between two groups of items rather than an item.</summary>
    public bool IsSeparator { get; init; }

    /// <summary>
    /// Whether the item throws something away or can't be taken back (delete, discard, remove), written in the skin's
    /// error colour so it reads as one before it's picked. It goes last in its menu, after a rule, by convention.
    /// </summary>
    public bool IsDestructive { get; set; }

    /// <summary>
    /// Whether the item is left out of the menu it's in when that is drawn, for one that's rare enough to only want the
    /// command palette and its key. It is still in <see cref="Menu.Everything"/>, and its key still goes off.
    /// </summary>
    public bool PaletteOnly { get; set; }

    internal bool Shown => IsVisible?.Invoke() ?? true;

    /// <summary>Whether the item is drawn in its menu right now, there and not one for the palette alone.</summary>
    public bool Listed => !PaletteOnly && Shown;

    internal bool Enabled => !IsSeparator && Shown && (IsEnabled?.Invoke() ?? true);
}

/// <summary>
/// A list of things to do, one of the menus of a <see cref="MenuBar"/> (a word on the bar and the list that opens
/// under it), what a <see cref="ContextMenu"/> shows, or a menu inside another one.
/// </summary>
public sealed class Menu(string title)
{
    private readonly List<MenuItem> items = [];

    /// <summary>The word on the bar, or what the item that opens it says.</summary>
    public string Title { get; set; } = title;

    /// <summary>
    /// Asked whenever the bar is drawn. Whether the menu is on it right now, for a menu that only means something
    /// some of the time. Always, if nothing is set.
    /// </summary>
    public Func<bool>? IsVisible { get; set; }

    public IReadOnlyList<MenuItem> Items => items;

    internal bool Shown => IsVisible?.Invoke() ?? true;

    /// <summary>
    /// Whether the item at an index is there right now. A rule is only there between two items that are, so a group
    /// with all of its items hidden doesn't leave two rules in a row, and nothing starts or ends on one.
    /// </summary>
    public bool IsShown(int index)
    {
        if (!items[index].IsSeparator)
            return items[index].Listed;

        // The nearest item before it that is there has to be a real one, a rule there means this one isn't needed
        bool before = false;
        for (int i = index - 1; i >= 0; i--)
        {
            if (!items[i].IsSeparator && !items[i].Listed)
                continue;

            before = !items[i].IsSeparator;
            break;
        }

        if (!before)
            return false;

        for (int i = index + 1; i < items.Count; i++)
        {
            if (!items[i].IsSeparator && items[i].Listed)
                return true;
        }

        return false;
    }

    /// <summary>Whether anything of the menu that does something is drawn in it right now, what an item opening it goes by.</summary>
    public bool HasShown() => items.Any(item => !item.IsSeparator && item.Listed);

    /// <summary>How many rows the menu shows right now, rules and all, for keeping a menu short enough to read.</summary>
    public int ShownCount()
    {
        int count = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (IsShown(i))
                count++;
        }

        return count;
    }

    /// <summary>Adds something to do at the end of the menu.</summary>
    /// <param name="icon">One of the icons every skin has (<see cref="UISymbols"/>) in front of it.</param>
    /// <param name="destructive">Whether it throws something away, see <see cref="MenuItem.IsDestructive"/>.</param>
    /// <returns>The item, to say more about (when it can be picked, whether it is checked).</returns>
    public MenuItem Add(string label, Action picked, string shortcut = "", string icon = "", bool destructive = false)
    {
        var item = new MenuItem { Label = label, OnPicked = picked, Shortcut = shortcut, Icon = icon, IsDestructive = destructive };
        items.Add(item);
        return item;
    }

    /// <summary>
    /// Adds an item made before there was a menu to put it in at the end of the menu, for a program that keeps its
    /// commands somewhere of their own and puts the menus together from them.
    /// </summary>
    /// <returns>The item.</returns>
    public MenuItem Add(MenuItem item)
    {
        items.Add(item);
        return item;
    }

    /// <summary>Adds a menu of its own at the end of this one, which opens to the side when the pointer rests on it.</summary>
    /// <returns>The menu, to add to.</returns>
    public Menu AddMenu(string title, string icon = "")
    {
        var submenu = new Menu(title);
        items.Add(new MenuItem { Label = title, Icon = icon, Submenu = submenu });
        return submenu;
    }

    /// <summary>Adds a rule after what is there, unless there is nothing there or a rule already.</summary>
    public void AddSeparator()
    {
        if (items.Count > 0 && !items[^1].IsSeparator)
            items.Add(new MenuItem { IsSeparator = true });
    }

    /// <summary>Empties the menu, for one whose items are put together again when what they stand for changes.</summary>
    public void Clear() => items.Clear();

    /// <summary>Every item of the menu and of the menus inside of it that does something, with the titles of the menus it is in.</summary>
    public IEnumerable<(MenuItem Item, string Path)> Everything(string path = "")
    {
        string here = path.Length > 0 ? $"{path} > {Title}" : Title;
        foreach (MenuItem item in items)
        {
            if (item.Submenu is { } submenu)
            {
                foreach (var inside in submenu.Everything(here))
                    yield return inside;
            }
            else if (!item.IsSeparator && item.OnPicked is not null)
            {
                yield return (item, here);
            }
        }
    }
}

/// <summary>
/// A strip of menus. A row of words, each of which opens a list of things to do under it. Clicking a word opens
/// its menu, clicking an item does what it says and closes it, clicking anywhere else closes it without doing
/// anything. An item can be a menu of its own, which opens to the side as the pointer goes over it. The menus are
/// put together in code.
/// <code>
/// Menu file = bar.AddMenu("File");
/// file.Add("Open...", OpenPressed, "Ctrl+O", icon: "folder-opened");
/// file.AddSeparator();
/// file.Add("Save", Save).IsEnabled = () => document.IsChanged;
/// Menu recent = file.AddMenu("Open recent");
/// </code>
/// A menu that only means something some of the time says so with <see cref="Menu.IsVisible"/> and is left off the
/// bar the rest of it. A layout only says where the bar goes (<c>compositor.menu_bar</c>), what is in it is up to the program.
/// </summary>
public partial class MenuBar : UIComponent
{
    private const float TITLE_PADDING = 12.0f;

    private readonly List<Menu> menus = [];
    private int open = -1;

    // The list of the open menu, and the lists open off it
    private MenuPanel? panel;

    public IReadOnlyList<Menu> Menus => menus;

    /// <summary>
    /// How wide the bar may be, 0 for as wide as all its words. With less, the words that don't fit go into a last
    /// menu at its end, each of them a menu of its own in it, VS Code's way. A title bar says it for the menus in it, so
    /// its buttons at the right end are never written over (Hex at 200% had Help under its Dawn and Dusk).
    /// </summary>
    public float Room { get; set; }

    // The menu at the end of the bar the words that didn't fit went into, and the first of the menus in it (as many as
    // there are when none are), worked out again only when that changes
    private readonly Menu more = new("\u2026");
    private int folded = int.MaxValue;
    private bool anyFolded;
    private (float Room, UIFont? Font, float Scale, int Count, int ShownHash, int TitlesHash) foldedFor;

    // Cached shown titles, measured widths, and horizontal offsets for each bar slot (menus + more) so Paint,
    // TitleBounds, ShownWidth, and Update never re-measure O(N^2) or allocate TitleShown strings per tick
    private string[] cachedRawTitles = [];
    private string[] cachedShownTitles = [];
    private float[] cachedTitleWidths = [];
    private float[] cachedTitleOffsets = [];
    private UIFont? cachedMeasureFont;
    private float cachedMeasureScale;
    private float cachedTotalWidth;

    /// <summary>The menus that didn't fit on the bar and are in its last menu instead, how many.</summary>
    public int FoldedCount => anyFolded ? menus.Count - folded : 0;

    /// <summary>The last menu with the menus that didn't fit, null while they all do.</summary>
    public Menu? FoldedMenu => anyFolded ? more : null;

    /// <summary>Where the word of the last menu is on the bar, empty while every menu fits.</summary>
    public UIRect FoldedBounds => TitleBounds(menus.Count);

    // The words of the bar, its menus and then the last one
    private int Count => menus.Count + 1;

    private Menu At(int index) => index < menus.Count ? menus[index] : more;

    // Whether a word is on the bar right now, a menu that's shown and fits, or the last one while some didn't
    private bool OnBar(int index) => index < menus.Count ? index < folded && menus[index].Shown : anyFolded;

    // Which menus fit in the room, and the last menu filled with the rest when that changed. Also refreshes cached
    // shown titles, measured widths, and horizontal offsets in one O(N) pass.
    private void Fold(UISkin skin)
    {
        float scale = TextScaleOf(skin);
        var shownHash = new HashCode();
        var titlesHash = new HashCode();
        for (int i = 0; i < menus.Count; i++)
        {
            shownHash.Add(menus[i].Shown);
            titlesHash.Add(menus[i].Title);
        }

        var key = (Room, skin.Font, scale, menus.Count, shownHash.ToHashCode(), titlesHash.ToHashCode());
        if (key == foldedFor)
            return;
        foldedFor = key;

        int totalSlots = Count;
        bool fontOrScaleChanged = cachedMeasureFont != skin.Font || cachedMeasureScale != scale;
        if (cachedRawTitles.Length < totalSlots)
        {
            int previousLength = cachedRawTitles.Length;
            Array.Resize(ref cachedRawTitles, totalSlots);
            Array.Resize(ref cachedShownTitles, totalSlots);
            Array.Resize(ref cachedTitleWidths, totalSlots);
            Array.Resize(ref cachedTitleOffsets, totalSlots);
            for (int i = previousLength; i < totalSlots; i++)
            {
                cachedRawTitles[i] = string.Empty;
                cachedShownTitles[i] = string.Empty;
            }
        }

        for (int i = 0; i < totalSlots; i++)
        {
            string raw = At(i).Title;
            if (fontOrScaleChanged || !ReferenceEquals(cachedRawTitles[i], raw))
            {
                string shown = TitleShown(raw);
                cachedRawTitles[i] = raw;
                cachedShownTitles[i] = shown;
                cachedTitleWidths[i] = skin.Font.Measure(shown, scale, markup: false).X + TITLE_PADDING * 2.0f;
            }
        }

        cachedMeasureFont = skin.Font;
        cachedMeasureScale = scale;

        int at = menus.Count;
        if (Room > 0.0f)
        {
            float whole = 0.0f;
            for (int i = 0; i < menus.Count; i++)
            {
                if (menus[i].Shown)
                    whole += cachedTitleWidths[i];
            }

            if (whole > Room + 0.5f)
            {
                float room = Room - cachedTitleWidths[menus.Count], used = 0.0f;
                for (at = 0; at < menus.Count; at++)
                {
                    if (!menus[at].Shown)
                        continue;
                    float width = cachedTitleWidths[at];
                    if (used + width > room)
                        break;
                    used += width;
                }
            }
        }

        bool any = false;
        for (int i = at; i < menus.Count && !any; i++)
            any = menus[i].Shown;

        if (at != folded || any != anyFolded)
        {
            // A menu open on the bar that went into the last one is shut, its word isn't there to hang from
            if ((open >= at && open < menus.Count) || (open == menus.Count && !any))
            {
                open = -1;
                panel = null;
            }

            folded = at;
            anyFolded = any;
            more.Clear();
            for (int i = at; i < menus.Count; i++)
                more.Add(new MenuItem { Label = cachedShownTitles[i], Submenu = menus[i], IsVisible = menus[i].IsVisible });
        }

        float offset = 0.0f;
        for (int i = 0; i < totalSlots; i++)
        {
            cachedTitleOffsets[i] = offset;
            if (OnBar(i))
                offset += cachedTitleWidths[i];
        }

        cachedTotalWidth = offset;
    }

    /// <summary>How wide the words on the bar are now, the menus that fit the <see cref="Room"/> and the last one.</summary>
    public float ShownWidth(UISkin skin)
    {
        Fold(skin);
        return cachedTotalWidth;
    }

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>The menu that is open right now, null if none is.</summary>
    public Menu? OpenMenu => IsPopupOpen && open >= 0 && open < Count ? At(open) : null;

    /// <summary>The menu open off an item of the open menu (the deepest, when they go further), null for none.</summary>
    public Menu? OpenSubmenu => OpenMenu is not null ? Panel?.OpenSubmenu : null;

    protected override bool HitTestVisible => true;

    /// <summary>Adds a menu at the end of the bar.</summary>
    public Menu AddMenu(string title)
    {
        var menu = new Menu(title);
        menus.Add(menu);
        return menu;
    }

    /// <summary>The menu with a title, null if the bar has none by it.</summary>
    public Menu? Find(string title)
    {
        int index = IndexOfMenu(title);
        return index >= 0 ? menus[index] : null;
    }

    private int IndexOfMenu(string title)
    {
        for (int i = 0; i < menus.Count; i++)
        {
            if (menus[i].Title == title)
                return i;
        }
        return -1;
    }

    private UISkin? Skin => Module?.Compositor.Skin;

    private float TextScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    protected override Vector2 Measure(UISkin skin)
    {
        return new Vector2(ShownWidth(skin), MenuPanel.RowHeight(skin, TextScale));
    }

    /// <summary>Where the word of one of the menus is on the bar, in the space of the module. Empty for one that isn't on it.</summary>
    public UIRect TitleBounds(int index)
    {
        if (Skin is not { } skin || index < 0 || index >= Count)
            return default;

        Fold(skin);
        if (!OnBar(index))
            return default;

        return TitleBoundsCached(index);
    }

    private UIRect TitleBoundsCached(int index)
    {
        float left = Bounds.Min.X + cachedTitleOffsets[index];
        return new UIRect(new Vector2(left, Bounds.Min.Y), new Vector2(left + cachedTitleWidths[index], Bounds.Max.Y));
    }

    /// <summary>Where the word of a menu is on the bar, by what it says.</summary>
    public UIRect TitleBounds(string title) => TitleBounds(IndexOfMenu(title));

    // The list of the open menu, made when it opens and again if the skin changes under it
    private MenuPanel? Panel
    {
        get
        {
            if (OpenMenu is not { } menu || Skin is not { } skin || Module is not { } module)
                return null;

            if (panel is null || panel.Menu != menu)
            {
                UIRect title = TitleBounds(open);
                panel = new MenuPanel(menu, skin, TextScale, MenuPlacement.Below, title, module.Frame, title.Width);
            }

            return panel;
        }
    }

    /// <summary>Where the list of the menu that is open is, in the space of the module. Empty while none is.</summary>
    public UIRect ListBounds => Panel?.Bounds ?? default;

    /// <summary>Whether the list of the open menu is taller than the room under the bar and scrolls.</summary>
    public bool ListScrolls => Panel?.Scrolls ?? false;

    /// <summary>Where every list open now is, the menu's and the ones open off it, in the space of the module.</summary>
    public IEnumerable<UIRect> OpenListBounds => Panel?.AllBounds() ?? [];

    // The wheel scrolls the list under the pointer, one too tall for the window
    protected internal override bool OnScroll(float delta) => Skin is { } skin && Panel is { } shown && shown.Wheel(skin, Pointer, delta);
    protected internal override bool UsesScrollSpeed => true;

    /// <summary>Where one of the items of the open menu is, counted from the top and with the separators counted too.</summary>
    public UIRect ItemBounds(int index) => Skin is { } skin && Panel is { } shown ? shown.ItemBounds(skin, index) : default;

    /// <summary>
    /// Where an item of the open menu, or of a menu open off it, is by what it says, scrolled into view first unless
    /// <paramref name="reveal"/> says not to (then empty while it is scrolled out of view). Empty if there is no such item.
    /// </summary>
    public UIRect ItemBounds(string label, bool reveal = true) => Skin is { } skin && Panel is { } shown ? shown.ItemBounds(skin, label, reveal) : default;

    /// <summary>Opens the menu of an item of the open menu, by what it says, as resting the pointer on it would.</summary>
    public bool OpenSubmenuOf(string label) =>
        Skin is { } skin && Module is { } module && Panel is { } shown && shown.OpenSubmenuOf(skin, module.Frame, label);

    /// <summary>Opens one of the menus, closing whichever was open.</summary>
    public void Open(int index)
    {
        if (index < 0 || index >= Count || !EnabledInHierarchy || TitleBounds(index).Width <= 0.0f)
            return;

        open = index;
        panel = null;
        armed = false;
        OpenPopup();
    }

    // The list that was showing as it was put away, painted going, see PaintClosing
    private MenuPanel? closingPanel;

    protected internal override void PopupClosed()
    {
        closingPanel = panel ?? closingPanel;
        panel = null;
        armed = false;
    }

    protected internal override bool PaintClosing(UIDrawList list, ref float closedAt)
    {
        if (closingPanel is not { } going || !list.BeginExit(ref closedAt, new Vector2(going.Bounds.Min.X, going.Bounds.Max.Y), new Vector2(0.0f, 6.0f)))
        {
            closingPanel = null;
            return false;
        }

        // Nothing under the pointer, it's going
        going.Paint(list, new Vector2(float.NegativeInfinity), Glass ?? Module?.Compositor.PopupGlass);
        list.EndExit();
        return true;
    }

    public void Close()
    {
        closingPanel = panel ?? closingPanel;
        open = -1;
        panel = null;
        armed = false;
        ClosePopup();
    }

    private Vector2 Pointer => Module is { } owner ? owner.ToLocal(owner.Compositor.Pointer.Position) : default;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = TextScaleOf(skin);
        Vector2 pointer = Pointer;

        Fold(skin);
        for (int i = 0; i < Count; i++)
        {
            list.BeginPart(i);
            if (!OnBar(i))
                continue;

            Menu menu = At(i);
            UIRect title = TitleBoundsCached(i);
            bool shown = OpenMenu == menu || (armed && hot == i);

            // The word of the open menu stays lit, the others light up under the pointer
            // always there, see-through when not lit, or the quads after it shift (ToggleButton has why)
            list.Box(title, shown ? skin.TextColor with { W = ListBox.PICKED } : IsHovered && title.Contains(pointer) ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

            string shownTitle = cachedShownTitles[i];
            list.Text(shownTitle, title, Origin.Center, scale, skin.TextColor, markup: false);
            PaintMnemonic(list, skin, menu, shownTitle, title, scale);
        }

        list.EndParts();
    }

    protected internal override void PaintPopup(UIDrawList list)
    {
        if (Panel is not { } shown || Module is not { } module)
            return;

        Vector2 pointer = Pointer;
        shown.Track(list.Skin, module.Frame, pointer);
        shown.Paint(list, pointer, Glass ?? module.Compositor.PopupGlass);
    }

    protected override void Update(float dt)
    {
        UpdateAlt();

        // Going along the bar with a menu open opens the menu of the word the pointer is on, the way every menu bar does.
        // Not while the keys have it and the pointer hasn't moved, or Right from the menu under it opened it again
        if (OpenMenu is null || !IsHovered || !PointerMovedSinceKeys)
            return;

        Vector2 pointer = Pointer;
        for (int i = 0; i < Count; i++)
        {
            if (i != open && TitleBounds(i).Contains(pointer))
            {
                Open(i);
                return;
            }
        }
    }

    protected internal override bool PopupContains(Vector2 point) => Panel?.Contains(point) ?? false;

    protected internal override void OnPointerUp(Vector2 point)
    {
        if (!EnabledInHierarchy)
            return;

        // On the bar. The word that was clicked opens its menu, or closes it if it was open
        if (Bounds.Contains(point))
        {
            for (int i = 0; i < Count; i++)
            {
                if (!TitleBounds(i).Contains(point))
                    continue;

                if (OpenMenu == At(i))
                    Close();
                else
                    Open(i);
                return;
            }

            Close();
            return;
        }

        if (Panel is not { } shown || Skin is not { } skin)
            return;

        MenuItem? item = shown.Pick(skin, point, out bool inside);

        // A separator, a menu of its own or something that can't be picked right now leaves the menu open
        if (item is null && inside)
            return;

        // Let go of somewhere else never mind then
        Close();
        item?.OnPicked?.Invoke();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
