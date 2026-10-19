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

    /// <summary>One of the icons every skin has (<see cref="UISymbols"/>) in front of it. Empty for none.</summary>
    public string Icon { get; set; } = string.Empty;

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

    internal bool Shown => IsVisible?.Invoke() ?? true;

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

    /// <summary>Adds something to do at the end of the menu.</summary>
    /// <param name="icon">One of the icons every skin has (<see cref="UISymbols"/>) in front of it.</param>
    /// <returns>The item, to say more about (when it can be picked, whether it is checked).</returns>
    public MenuItem Add(string label, Action picked, string shortcut = "", string icon = "")
    {
        var item = new MenuItem { Label = label, OnPicked = picked, Shortcut = shortcut, Icon = icon };
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
public class MenuBar : UIComponent
{
    private const float TITLE_PADDING = 12.0f;

    private readonly List<Menu> menus = [];
    private int open = -1;

    // The list of the open menu, and the lists open off it
    private MenuPanel? panel;

    public IReadOnlyList<Menu> Menus => menus;

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>The menu that is open right now, null if none is.</summary>
    public Menu? OpenMenu => IsPopupOpen && open >= 0 && open < menus.Count ? menus[open] : null;

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
    public Menu? Find(string title) => menus.Find(menu => menu.Title == title);

    private UISkin? Skin => Module?.Compositor.Skin;

    private float TextScaleOf(UISkin skin) => TextScale > 0.0f ? TextScale : skin.TextScale;

    private float TitleWidth(UISkin skin, Menu menu) => skin.Font.Measure(menu.Title, TextScaleOf(skin), markup: false).X + TITLE_PADDING * 2.0f;

    protected override Vector2 Measure(UISkin skin)
    {
        float width = 0.0f;
        foreach (Menu menu in menus)
        {
            if (menu.Shown)
                width += TitleWidth(skin, menu);
        }

        return new Vector2(width, MenuPanel.RowHeight(skin, TextScale));
    }

    /// <summary>Where the word of one of the menus is on the bar, in the space of the module. Empty for one that isn't on it.</summary>
    public UIRect TitleBounds(int index)
    {
        if (Skin is not { } skin || index < 0 || index >= menus.Count || !menus[index].Shown)
            return default;

        float left = Bounds.Min.X;
        for (int i = 0; i < index; i++)
        {
            if (menus[i].Shown)
                left += TitleWidth(skin, menus[i]);
        }

        return new UIRect(new Vector2(left, Bounds.Min.Y), new Vector2(left + TitleWidth(skin, menus[index]), Bounds.Max.Y));
    }

    /// <summary>Where the word of a menu is on the bar, by what it says.</summary>
    public UIRect TitleBounds(string title) => TitleBounds(menus.FindIndex(menu => menu.Title == title));

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
                panel = new MenuPanel(menu, skin, TextScale, new Vector2(title.Min.X, title.Min.Y), module.Frame, title.Width);
            }

            return panel;
        }
    }

    /// <summary>Where the list of the menu that is open is, in the space of the module. Empty while none is.</summary>
    public UIRect ListBounds => Panel?.Bounds ?? default;

    /// <summary>Where one of the items of the open menu is, counted from the top and with the separators counted too.</summary>
    public UIRect ItemBounds(int index) => Skin is { } skin && Panel is { } shown ? shown.ItemBounds(skin, index) : default;

    /// <summary>Where an item of the open menu, or of a menu open off it, is by what it says. Empty if there is no such item.</summary>
    public UIRect ItemBounds(string label) => Skin is { } skin && Panel is { } shown ? shown.ItemBounds(skin, label) : default;

    /// <summary>Opens the menu of an item of the open menu, by what it says, as resting the pointer on it would.</summary>
    public bool OpenSubmenuOf(string label) =>
        Skin is { } skin && Module is { } module && Panel is { } shown && shown.OpenSubmenuOf(skin, module.Frame, label);

    /// <summary>Opens one of the menus, closing whichever was open.</summary>
    public void Open(int index)
    {
        if (index < 0 || index >= menus.Count || !EnabledInHierarchy || !menus[index].Shown)
            return;

        open = index;
        panel = null;
        OpenPopup();
    }

    // The list that was showing as it was put away, painted going, see PaintClosing
    private MenuPanel? closingPanel;

    protected internal override void PopupClosed()
    {
        closingPanel = panel ?? closingPanel;
        panel = null;
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
        ClosePopup();
    }

    private Vector2 Pointer => Module is { } owner ? owner.ToLocal(owner.Compositor.Pointer.Position) : default;

    protected override void Paint(UIDrawList list)
    {
        UISkin skin = list.Skin;
        float scale = TextScaleOf(skin);
        Vector2 pointer = Pointer;

        for (int i = 0; i < menus.Count; i++)
        {
            list.BeginPart(i);
            if (!menus[i].Shown)
                continue;

            UIRect title = TitleBounds(i);
            bool shown = OpenMenu == menus[i];

            // The word of the open menu stays lit, the others light up under the pointer
            // always there, see-through when not lit, or the quads after it shift (ToggleButton has why)
            list.Box(title, shown ? skin.AccentColor with { W = 0.35f } : IsHovered && title.Contains(pointer) ? skin.HoverColor : skin.HoverColor with { W = 0.0f });

            list.Text(menus[i].Title, title, Origin.Center, scale, shown ? skin.AccentColor : skin.TextColor, markup: false);
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
        // Going along the bar with a menu open opens the menu of the word the pointer is on, the way every menu bar does
        if (OpenMenu is null || !IsHovered)
            return;

        Vector2 pointer = Pointer;
        for (int i = 0; i < menus.Count; i++)
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
            for (int i = 0; i < menus.Count; i++)
            {
                if (!TitleBounds(i).Contains(point))
                    continue;

                if (OpenMenu == menus[i])
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
