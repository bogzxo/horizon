using Horizon.UI;

using Key = Silk.NET.Input.Key;

namespace Horizon.UI.Components;

/// <summary>What a command's key does besides being written next to it in its menu.</summary>
public enum UIKeyUse
{
    /// <summary>Nothing, something else answers the key (the code editor its own editing keys, or a key bound on its own).</summary>
    Shown,

    /// <summary>Picks the command, except while something is being typed into.</summary>
    Bound,

    /// <summary>Picks the command, typing or not.</summary>
    BoundWhileTyping
}

/// <summary>
/// Everything there is to do from a menu bar and the keys, put together out of what each part of a program adds of its
/// own rather than written in one place. The bar's menus are declared first, each with its groups (the runs of items
/// between two rules), then each part adds its commands to a menu and a group by name ("Edit/find"), with the key the
/// menu shows and whether pressing it picks the command. Keys that aren't a command of a menu (Ctrl+Tab, the arrows
/// nudging what is selected) are bound on their own. <see cref="Build"/> puts the bar together once all of it is in, the
/// groups in the order they were declared and a command after the ones added before it in its group.
/// </summary>
public sealed class UICommands
{
    private sealed class Declared(string title, string[] groups)
    {
        public string Title { get; } = title;
        public string[] Groups { get; } = groups;
        public Func<bool>? Shown { get; set; }
        public List<MenuItem>[] Items { get; } = [.. groups.Select(_ => new List<MenuItem>())];

        // The menus of its own declared in a group of it, each after what was added to that group
        public List<Submenu> Submenus { get; } = [];
    }

    // A menu of its own declared in a group of a menu of the bar, commands go in its groups the way they go in the bar's
    private sealed class Submenu(string name, int group, Menu menu, MenuItem row, string[] groups)
    {
        public string Name { get; } = name;
        public int Group { get; } = group;
        public Menu Menu { get; } = menu;
        public MenuItem Row { get; } = row;
        public string[] Groups { get; } = groups;
        public List<MenuItem>[] Items { get; } = [.. groups.Select(_ => new List<MenuItem>())];
    }

    private readonly List<Declared> menus = [];

    // Every command added, with the menu it went in and what its key does, for a list of keys to say when one goes off
    private readonly Dictionary<MenuItem, (Declared Menu, UIKeyUse Use)> added = new(ReferenceEqualityComparer.Instance);

    // The menus of their own added to a menu, what's filled into them is that menu's too (the Layout menu's Convert To)
    private readonly List<(Menu Submenu, Declared Menu)> submenus = [];

    /// <summary>The keys, the commands' and the ones bound on their own, set off every update.</summary>
    public UIShortcuts Keys { get; } = new();

    /// <summary>A menu of the bar, after the ones declared before it, with the names of its groups in their order.</summary>
    public void Declare(string title, params string[] groups) => menus.Add(new Declared(title, groups));

    /// <summary>
    /// A menu of its own in a group of a menu of the bar ("File/preferences"), which opens to the side, with groups of its
    /// own that commands are added to by name ("File/preferences/prefs:main", or "Help/developer/dev" for one with no
    /// groups). Its commands are the bar menu's, their keys go off the same, and it is only there while something in
    /// it is. It goes after what is added to its group.
    /// </summary>
    /// <param name="name">What commands call it by in where they go.</param>
    /// <param name="title">What its item says.</param>
    public Menu DeclareSubmenu(string where, string name, string title, string icon = "", params string[] groups)
    {
        var (menu, group) = MenuAndGroup(where);
        var submenu = new Menu(title);
        var row = new MenuItem { Label = title, Icon = icon, Submenu = submenu, IsVisible = submenu.HasShown };
        menu.Submenus.Add(new Submenu(name, group, submenu, row, groups.Length > 0 ? groups : [string.Empty]));
        return submenu;
    }

    /// <summary>
    /// Has a menu on the bar only while something is so (Hex's Layout menu while a layout is in its designer). Said before
    /// its commands are added, their keys then go before the same keys of the menus that are always there.
    /// </summary>
    public void ShowMenuWhen(string title, Func<bool> shown) => MenuCalled(title).Shown = shown;

    /// <summary>
    /// Adds a command to a group of a menu ("Edit/find"), after what was added to the group before it.
    /// </summary>
    /// <param name="key">
    /// The key the menu writes next to it, as keys.hor does ("Ctrl+Shift+F"), empty for none. Two keys with a gap between
    /// them are a chord, the first pressed and then the second ("Ctrl+K Z").
    /// </param>
    /// <param name="use">Whether pressing the key picks the command, and while typing too.</param>
    /// <returns>The item, to say more about (when it can be picked, whether it is checked).</returns>
    public MenuItem Add(string where, string label, Action picked, string key = "", string icon = "", UIKeyUse use = UIKeyUse.Shown)
    {
        var item = new MenuItem { Label = label, OnPicked = picked, Shortcut = key, Icon = icon };
        GroupAt(where).Add(item);
        Declared menu = BarMenuOf(where);
        added[item] = (menu, use);

        if (use != UIKeyUse.Shown)
        {
            var (read, then) = UIKeys.ReadChord(key) ?? throw new ArgumentException($"{label}: \"{key}\" isn't a key", nameof(key));

            // A key written without shift goes off with shift held too, unless the key with shift is another command's. It
            // picks the command only while the command can be picked, its menu on the bar and it in the menu and not
            // greyed out, or two commands on one key both went off (Hex's Ctrl+G went to a line and grouped in the
            // designer at once). One it can't pick it passes on to the next command on the key, Ctrl+Shift+G with no
            // group selected is Source Control's and not an Ungroup saying there's nothing to ungroup
            bool Pressed()
            {
                if (!(menu.Shown?.Invoke() ?? true) || !item.Enabled)
                    return false;

                picked();
                return true;
            }

            // A chord's first key is held exactly as it's written, shift or not
            bool? shift = then is not null ? read.Shift : read.Shift ? true : null;

            // A command of a menu that's only on the bar some of the time (Hex's Layout menu) is about what's in front
            // of you right now, and has its key before one that's always there
            Keys.Add(read.Key, Pressed, read.Control, use == UIKeyUse.BoundWhileTyping, shift, read.Alt, first: menu.Shown is not null, then: then);
        }

        return item;
    }

    /// <summary>
    /// Where a command's key means something, the title of its menu when that menu is only on the bar some of the time
    /// (Hex's Layout menu in the designer, its Selection menu in the code editor), null for one that's always there or
    /// an item that wasn't added here. One filled into a menu of its own (<see cref="AddMenu"/>) is in that menu's.
    /// </summary>
    public string? ContextOf(MenuItem item) => MenuOf(item) is { Shown: not null } menu ? menu.Title : null;

    /// <summary>What a command's key was added to do, <see cref="UIKeyUse.Shown"/> for an item that wasn't added here.</summary>
    public UIKeyUse UseOf(MenuItem item) => added.TryGetValue(item, out var where) ? where.Use : UIKeyUse.Shown;

    /// <summary>
    /// Whether a command could be picked right now, its menu on the bar and it in the menu and not greyed out. What a key
    /// put on more than one command goes by. An item that wasn't added here goes by itself alone.
    /// </summary>
    public bool CanPick(MenuItem item) => (MenuOf(item)?.Shown?.Invoke() ?? true) && item.Enabled;

    // The menu of the bar an item is in, added to it or filled into a menu of its own added to it, null for neither
    private Declared? MenuOf(MenuItem item)
    {
        if (added.TryGetValue(item, out var where))
            return where.Menu;

        foreach (var (submenu, menu) in submenus)
        {
            if (Holds(submenu, item))
                return menu;
        }

        return null;
    }

    // Whether an item is in a menu, or in one of the menus in it
    private static bool Holds(Menu menu, MenuItem item)
    {
        foreach (MenuItem inside in menu.Items)
        {
            if (ReferenceEquals(inside, item) || (inside.Submenu is { } deeper && Holds(deeper, item)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Adds a menu of its own to a group of a menu, which opens to the side, to fill. Its item is there while the menu
    /// says it is (<see cref="Menu.IsVisible"/>).
    /// </summary>
    public Menu AddMenu(string where, string title, string icon = "")
    {
        var submenu = new Menu(title);
        GroupAt(where).Add(new MenuItem { Label = title, Icon = icon, Submenu = submenu, IsVisible = () => submenu.Shown });
        submenus.Add((submenu, BarMenuOf(where)));
        return submenu;
    }

    /// <summary>A key that does something without being a command of a menu. See <see cref="UIShortcuts.Add"/>.</summary>
    public void Bind(Key key, Action pressed, bool control = false, bool whileTyping = false, bool? shift = null, bool alt = false, UIKeyStroke? then = null) =>
        Keys.Add(key, pressed, control, whileTyping, shift, alt, then);

    /// <summary>Puts the menus on the bar, with a rule between two groups that have something in them.</summary>
    public void Build(MenuBar bar)
    {
        foreach (Declared declared in menus)
        {
            Menu menu = bar.AddMenu(declared.Title);
            menu.IsVisible = declared.Shown;
            for (int group = 0; group < declared.Items.Length; group++)
            {
                // What was added to the group, then the menus of its own declared in it
                List<MenuItem> items = [.. declared.Items[group]];
                foreach (Submenu submenu in declared.Submenus.Where(submenu => submenu.Group == group))
                {
                    Fill(submenu.Menu, submenu.Items);
                    items.Add(submenu.Row);
                }

                Fill(menu, [items]);
            }
        }
    }

    // Puts groups of items in a menu, a rule before each one that has something in it and isn't the first
    private static void Fill(Menu menu, List<MenuItem>[] groups)
    {
        foreach (List<MenuItem> group in groups)
        {
            if (group.Count == 0)
                continue;

            menu.AddSeparator();
            foreach (MenuItem item in group)
                menu.Add(item);
        }
    }

    private Declared MenuCalled(string title) =>
        menus.Find(menu => menu.Title == title) ?? throw new ArgumentException($"there is no menu called {title}", nameof(title));

    // The menu of the bar a place is in, the one a menu of its own in it is in for one of those
    private Declared BarMenuOf(string where)
    {
        int slash = where.IndexOf('/');
        if (slash < 0)
            throw new ArgumentException($"\"{where}\" isn't a menu and a group of it, Edit/find", nameof(where));

        return MenuCalled(where[..slash]);
    }

    // A menu of the bar and the index of a group of it, from "Edit/find"
    private (Declared Menu, int Group) MenuAndGroup(string where)
    {
        string[] parts = where.Split('/');
        Declared menu = BarMenuOf(where);
        int group = parts.Length >= 2 ? Array.IndexOf(menu.Groups, parts[1]) : -1;
        if (group < 0)
            throw new ArgumentException($"the {menu.Title} menu has no group called {(parts.Length >= 2 ? parts[1] : "")}", nameof(where));

        return (menu, group);
    }

    // The items of a group, a bar menu's ("Edit/find") or of a menu of its own in one ("File/preferences/prefs:main",
    // its first group when it says none)
    private List<MenuItem> GroupAt(string where)
    {
        string[] parts = where.Split('/');
        var (menu, group) = MenuAndGroup(where);
        if (parts.Length == 2)
            return menu.Items[group];

        if (parts.Length != 3)
            throw new ArgumentException($"\"{where}\" isn't a menu, a group and a menu of its own in it, File/preferences/prefs:main", nameof(where));

        int colon = parts[2].IndexOf(':');
        string name = colon < 0 ? parts[2] : parts[2][..colon];
        Submenu submenu = menu.Submenus.Find(submenu => submenu.Name == name && submenu.Group == group)
            ?? throw new ArgumentException($"the {menu.Title} menu has no menu called {name} in {parts[1]}", nameof(where));

        int inside = colon < 0 ? 0 : Array.IndexOf(submenu.Groups, parts[2][(colon + 1)..]);
        if (inside < 0)
            throw new ArgumentException($"{submenu.Menu.Title} has no group called {parts[2][(colon + 1)..]}", nameof(where));

        return submenu.Items[inside];
    }
}
