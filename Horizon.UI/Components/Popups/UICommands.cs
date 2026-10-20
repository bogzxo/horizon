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
    }

    private readonly List<Declared> menus = [];

    /// <summary>The keys, the commands' and the ones bound on their own, set off every update.</summary>
    public UIShortcuts Keys { get; } = new();

    /// <summary>A menu of the bar, after the ones declared before it, with the names of its groups in their order.</summary>
    public void Declare(string title, params string[] groups) => menus.Add(new Declared(title, groups));

    /// <summary>Has a menu on the bar only while something is so (Hex's Layout menu while a layout is in its designer).</summary>
    public void ShowMenuWhen(string title, Func<bool> shown) => MenuCalled(title).Shown = shown;

    /// <summary>
    /// Adds a command to a group of a menu ("Edit/find"), after what was added to the group before it.
    /// </summary>
    /// <param name="key">The key the menu writes next to it, as keys.hor does ("Ctrl+Shift+F"), empty for none.</param>
    /// <param name="use">Whether pressing the key picks the command, and while typing too.</param>
    /// <returns>The item, to say more about (when it can be picked, whether it is checked).</returns>
    public MenuItem Add(string where, string label, Action picked, string key = "", string icon = "", UIKeyUse use = UIKeyUse.Shown)
    {
        var item = new MenuItem { Label = label, OnPicked = picked, Shortcut = key, Icon = icon };
        GroupAt(where).Add(item);

        if (use != UIKeyUse.Shown)
        {
            var read = UIKeys.Read(key) ?? throw new ArgumentException($"{label}: \"{key}\" isn't a key", nameof(key));

            // A key written without shift goes off with shift held too, unless the key with shift is another command's
            Keys.Add(read.Key, picked, read.Control, use == UIKeyUse.BoundWhileTyping, read.Shift ? true : null, read.Alt);
        }

        return item;
    }

    /// <summary>Adds a menu of its own to a group of a menu, which opens to the side, to fill.</summary>
    public Menu AddMenu(string where, string title, string icon = "")
    {
        var submenu = new Menu(title);
        GroupAt(where).Add(new MenuItem { Label = title, Icon = icon, Submenu = submenu });
        return submenu;
    }

    /// <summary>A key that does something without being a command of a menu. See <see cref="UIShortcuts.Add"/>.</summary>
    public void Bind(Key key, Action pressed, bool control = false, bool whileTyping = false, bool? shift = null, bool alt = false) =>
        Keys.Add(key, pressed, control, whileTyping, shift, alt);

    /// <summary>Puts the menus on the bar, with a rule between two groups that have something in them.</summary>
    public void Build(MenuBar bar)
    {
        foreach (Declared declared in menus)
        {
            Menu menu = bar.AddMenu(declared.Title);
            menu.IsVisible = declared.Shown;
            foreach (List<MenuItem> group in declared.Items)
            {
                if (group.Count == 0)
                    continue;

                menu.AddSeparator();
                foreach (MenuItem item in group)
                    menu.Add(item);
            }
        }
    }

    private Declared MenuCalled(string title) =>
        menus.Find(menu => menu.Title == title) ?? throw new ArgumentException($"there is no menu called {title}", nameof(title));

    private List<MenuItem> GroupAt(string where)
    {
        int slash = where.IndexOf('/');
        if (slash < 0)
            throw new ArgumentException($"\"{where}\" isn't a menu and a group of it, Edit/find", nameof(where));

        Declared menu = MenuCalled(where[..slash]);
        int group = Array.IndexOf(menu.Groups, where[(slash + 1)..]);
        if (group < 0)
            throw new ArgumentException($"the {menu.Title} menu has no group called {where[(slash + 1)..]}", nameof(where));

        return menu.Items[group];
    }
}
