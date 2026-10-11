using System.Numerics;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// One line of a <see cref="Menu"/>, something to do or a rule between two groups of them.
/// </summary>
public sealed class MenuItem
{
    /// <summary>What the item says. Empty for a separator.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>What is written at the far end of the item, which is where the keys that do the same thing go.</summary>
    public string Shortcut { get; set; } = string.Empty;

    /// <summary>What picking the item does.</summary>
    public Action? OnPicked { get; set; }

    /// <summary>Asked whenever the menu is drawn or clicked. Whether the item can be picked right now. Always, if nothing is set.</summary>
    public Func<bool>? IsEnabled { get; set; }

    /// <summary>Asked whenever the menu is drawn. Whether the item has a mark in front of it, for one that switches something on and off.</summary>
    public Func<bool>? IsChecked { get; set; }

    /// <summary>Whether this is a rule between two groups of items rather than an item.</summary>
    public bool IsSeparator { get; init; }

    internal bool Enabled => !IsSeparator && (IsEnabled?.Invoke() ?? true);
}

/// <summary>
/// One of the menus of a <see cref="MenuBar"/>, a word on the bar and the list that opens under it.
/// </summary>
public sealed class Menu(string title)
{
    private readonly List<MenuItem> items = [];

    /// <summary>The word on the bar.</summary>
    public string Title { get; set; } = title;

    public IReadOnlyList<MenuItem> Items => items;

    /// <summary>Adds something to do at the end of the menu.</summary>
    /// <returns>The item, to say more about (when it can be picked, whether it is checked).</returns>
    public MenuItem Add(string label, Action picked, string shortcut = "")
    {
        var item = new MenuItem { Label = label, OnPicked = picked, Shortcut = shortcut };
        items.Add(item);
        return item;
    }

    /// <summary>Adds a rule after what is there, unless there is nothing there or a rule already.</summary>
    public void AddSeparator()
    {
        if (items.Count > 0 && !items[^1].IsSeparator)
            items.Add(new MenuItem { IsSeparator = true });
    }

    /// <summary>Empties the menu, for one whose items are put together again when what they stand for changes.</summary>
    public void Clear() => items.Clear();
}

/// <summary>
/// A strip of menus. A row of words, each of which opens a list of things to do under it. Clicking a word opens
/// its menu, clicking an item does what it says and closes it, clicking anywhere else closes it without doing
/// anything. The menus are put together in code.
/// <code>
/// Menu file = bar.AddMenu("File");
/// file.Add("Open...", OpenPressed, "Ctrl+O");
/// file.AddSeparator();
/// file.Add("Save", Save).IsEnabled = () => document.IsChanged;
/// </code>
/// A layout only says where the bar goes (<c>compositor.menu_bar</c>), what is in it is up to the program.
/// </summary>
public class MenuBar : UIComponent
{
    private const float BORDER = 2.0f;
    private const float TITLE_PADDING = 12.0f;
    private const float ITEM_PADDING = 12.0f;

    // The room kept in front of every item for the mark of the ones that are checked, and between an item and its shortcut
    private const float CHECK_WIDTH = 16.0f;
    private const float SHORTCUT_GAP = 28.0f;

    // How tall a separator is against an item
    private const float SEPARATOR_SHARE = 0.4f;

    private readonly List<Menu> menus = [];
    private int open = -1;

    public IReadOnlyList<Menu> Menus => menus;

    /// <summary>The scale of the text, or zero to use the skin's.</summary>
    public float TextScale { get; set; }

    /// <summary>The menu that is open right now, null if none is.</summary>
    public Menu? OpenMenu => IsPopupOpen && open >= 0 && open < menus.Count ? menus[open] : null;

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

    private float RowHeight(UISkin skin) => MathF.Round(skin.Font.LineHeight * TextScaleOf(skin) + skin.ButtonPadding.Total.Y * 0.6f);

    protected override Vector2 Measure(UISkin skin)
    {
        float width = 0.0f;
        foreach (Menu menu in menus)
            width += skin.Font.Measure(menu.Title, TextScaleOf(skin), markup: false).X + TITLE_PADDING * 2.0f;

        return new Vector2(width, RowHeight(skin));
    }

    /// <summary>Where the word of one of the menus is on the bar, in the space of the module.</summary>
    public UIRect TitleBounds(int index)
    {
        if (Skin is not { } skin || index < 0 || index >= menus.Count)
            return default;

        float left = Bounds.Min.X;
        for (int i = 0; i < index; i++)
            left += skin.Font.Measure(menus[i].Title, TextScaleOf(skin), markup: false).X + TITLE_PADDING * 2.0f;

        float width = skin.Font.Measure(menus[index].Title, TextScaleOf(skin), markup: false).X + TITLE_PADDING * 2.0f;
        return new UIRect(new Vector2(left, Bounds.Min.Y), new Vector2(left + width, Bounds.Max.Y));
    }

    /// <summary>Where the word of a menu is on the bar, by what it says.</summary>
    public UIRect TitleBounds(string title) => TitleBounds(menus.FindIndex(menu => menu.Title == title));

    /// <summary>Where the list of the menu that is open is, in the space of the module. Empty while none is.</summary>
    public UIRect ListBounds
    {
        get
        {
            if (OpenMenu is not { } menu || Skin is not { } skin)
                return default;

            float scale = TextScaleOf(skin), row = RowHeight(skin);
            float width = 0.0f, height = 0.0f;

            foreach (MenuItem item in menu.Items)
            {
                height += item.IsSeparator ? MathF.Round(row * SEPARATOR_SHARE) : row;

                float shortcut = item.Shortcut.Length > 0 ? skin.Font.Measure(item.Shortcut, scale, markup: false).X + SHORTCUT_GAP : 0.0f;
                width = MathF.Max(width, CHECK_WIDTH + skin.Font.Measure(item.Label, scale, markup: false).X + shortcut + ITEM_PADDING * 2.0f);
            }

            // Under its word, and no narrower than it
            UIRect title = TitleBounds(open);
            width = MathF.Max(width, title.Width) + BORDER * 2.0f;

            return new UIRect(
                new Vector2(title.Min.X, title.Min.Y - height - BORDER * 2.0f),
                new Vector2(title.Min.X + width, title.Min.Y));
        }
    }

    /// <summary>Where one of the items of the open menu is, counted from the top and with the separators counted too.</summary>
    public UIRect ItemBounds(int index)
    {
        if (OpenMenu is not { } menu || Skin is not { } skin || index < 0 || index >= menu.Items.Count)
            return default;

        UIRect list = ListBounds.Shrink(new UIEdges(BORDER));
        float row = RowHeight(skin);
        float top = list.Max.Y;

        for (int i = 0; i < index; i++)
            top -= menu.Items[i].IsSeparator ? MathF.Round(row * SEPARATOR_SHARE) : row;

        float height = menu.Items[index].IsSeparator ? MathF.Round(row * SEPARATOR_SHARE) : row;
        return new UIRect(new Vector2(list.Min.X, top - height), new Vector2(list.Max.X, top));
    }

    /// <summary>Where an item of the open menu is, by what it says. Empty if the open menu has no such item.</summary>
    public UIRect ItemBounds(string label)
    {
        if (OpenMenu is not { } menu)
            return default;

        for (int i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i].Label == label)
                return ItemBounds(i);
        }

        return default;
    }

    /// <summary>Opens one of the menus, closing whichever was open.</summary>
    public void Open(int index)
    {
        if (index < 0 || index >= menus.Count || !EnabledInHierarchy)
            return;

        open = index;
        OpenPopup();
    }

    public void Close()
    {
        open = -1;
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
            UIRect title = TitleBounds(i);
            bool shown = OpenMenu == menus[i];

            // The word of the open menu stays lit, the others light up under the pointer
            if (shown || (IsHovered && title.Contains(pointer)))
                list.Box(title, shown ? skin.AccentColor with { W = 0.35f } : skin.HoverColor);

            list.Text(menus[i].Title, title, Origin.Center, scale, shown ? skin.AccentColor : skin.TextColor, markup: false);
        }

        list.EndParts();
    }

    protected internal override void PaintPopup(UIDrawList list)
    {
        if (OpenMenu is not { } menu)
            return;

        UISkin skin = list.Skin;
        float scale = TextScaleOf(skin);

        // Whatever is under the list must not show through it.
        UIRect bounds = ListBounds;
        list.Box(bounds, new Vector4(skin.PanelColor.X, skin.PanelColor.Y, skin.PanelColor.Z, 1.0f));
        list.Frame(bounds, BORDER, skin.AccentColor);

        Vector2 pointer = Pointer;
        Vector4 dim = skin.TextColor * skin.DisabledTint;

        for (int i = 0; i < menu.Items.Count; i++)
        {
            // An item at a time, or the highlight of one has the rule after it blended with it for a tick
            list.BeginPart(i);
            MenuItem item = menu.Items[i];
            UIRect area = ItemBounds(i);

            if (item.IsSeparator)
            {
                float middle = MathF.Round(area.Center.Y);
                list.Rect(new UIRect(new Vector2(area.Min.X + ITEM_PADDING, middle), new Vector2(area.Max.X - ITEM_PADDING, middle + 1.0f)), dim);
                continue;
            }

            bool enabled = item.Enabled;
            if (enabled && area.Contains(pointer))
                list.Rect(area, skin.HoverColor);

            UIRect content = area.Shrink(new UIEdges(ITEM_PADDING, 0.0f));

            if (item.IsChecked?.Invoke() == true)
            {
                // A small square in the room every item keeps for it
                float side = MathF.Round(area.Height * 0.3f);
                Vector2 corner = new(content.Min.X, MathF.Round(content.Center.Y - side * 0.5f));
                list.Rect(new UIRect(corner, corner + new Vector2(side)), skin.AccentColor);
            }

            UIRect text = new(new Vector2(content.Min.X + CHECK_WIDTH, content.Min.Y), content.Max);
            list.Text(item.Label, text, Origin.Left, scale, enabled ? skin.TextColor : dim, markup: false);

            if (item.Shortcut.Length > 0)
                list.Text(item.Shortcut, text, Origin.Right, scale, dim, markup: false);
        }

        list.EndParts();
    }

    protected internal override bool PopupContains(Vector2 point) => OpenMenu is not null && ListBounds.Contains(point);

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

        if (OpenMenu is not { } menu)
            return;

        for (int i = 0; i < menu.Items.Count; i++)
        {
            if (!ItemBounds(i).Contains(point))
                continue;

            MenuItem item = menu.Items[i];

            // A separator or something that can't be picked right now leaves the menu open
            if (!item.Enabled)
                return;

            Close();
            item.OnPicked?.Invoke();
            return;
        }

        // Let go of somewhere else never mind then.
        Close();
    }

    protected override void DefineScript()
    {
        base.DefineScript();

        Expose("text_scale", () => TextScale, value => TextScale = value);
    }
}
