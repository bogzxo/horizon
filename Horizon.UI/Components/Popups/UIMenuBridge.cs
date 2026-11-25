using Horizon.Platform;

namespace Horizon.UI.Components;

/// <summary>
/// Turns the menus of a <see cref="MenuBar"/> (what <see cref="UICommands.Build"/> puts together) into the platform's own
/// menus (<see cref="PlatformMenu"/>), for a system menu bar that mirrors them. Pure: it makes the model and nothing
/// else, so it's tested without a window. A menu's items are read again each time the platform asks (it's about to
/// open), so a menu filled in as the program changes (recent files) and an item that comes and goes, or is hidden,
/// show as they are then, with the rules between groups as the in-window menu draws them (none leading, trailing or in
/// a row).
/// </summary>
public static class UIMenuBridge
{
    /// <summary>The menus on a bar, in its order, each of them on the platform's bar only while the menu says it's on the in-window one.</summary>
    /// <param name="roleOf">The role of the menu with a title (a Window menu's list of windows, a Help menu's search), none when null or when it says so.</param>
    public static IReadOnlyList<PlatformMenu> FromBar(MenuBar bar, Func<string, PlatformMenuRole>? roleOf = null)
    {
        ArgumentNullException.ThrowIfNull(bar);

        var menus = new List<PlatformMenu>();
        foreach (Menu menu in bar.Menus)
            menus.Add(FromMenu(menu, roleOf?.Invoke(menu.Title) ?? PlatformMenuRole.None, topLevel: true));

        return menus;
    }

    /// <summary>One menu, its items asked for again whenever the platform needs them.</summary>
    public static PlatformMenu FromMenu(Menu menu, PlatformMenuRole role = PlatformMenuRole.None) => FromMenu(menu, role, topLevel: false);

    private static PlatformMenu FromMenu(Menu menu, PlatformMenuRole role, bool topLevel) =>
        new(menu.Title, () => ItemsOf(menu), role) { Visible = topLevel ? () => menu.Shown : null };

    /// <summary>The items a menu shows right now, a rule only between two items that are there, and a menu inside it only while it has something in it.</summary>
    public static IReadOnlyList<PlatformMenuItem> ItemsOf(Menu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        var items = new List<PlatformMenuItem>();
        IReadOnlyList<MenuItem> all = menu.Items;
        for (int i = 0; i < all.Count; i++)
        {
            if (!menu.IsShown(i))
                continue;

            MenuItem item = all[i];
            items.Add(item.IsSeparator ? PlatformMenuItem.Separator() : ItemOf(item));
        }

        return items;
    }

    /// <summary>One item of a menu. Its state is the menu item's own, asked as the platform needs it.</summary>
    public static PlatformMenuItem ItemOf(MenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new PlatformMenuItem
        {
            Label = item.Label,
            Shortcut = item.Shortcut,
            Submenu = item.Submenu is { } submenu ? FromMenu(submenu) : null,
            Enabled = () => item.Enabled,
            Checked = item.IsChecked,
            Visible = () => item.Listed,
            Picked = item.OnPicked,
        };
    }
}
