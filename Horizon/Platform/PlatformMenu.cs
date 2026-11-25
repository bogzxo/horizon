namespace Horizon.Platform;

/// <summary>
/// What a menu, or an item of one, is for when the system has a word for it, so the platform can use the system's own
/// action and key for it instead of one of the program's. A role is for the ones a Mac's menu bar has (Hide, Quit, the
/// Window menu's list of windows), nothing on a platform without a native menu bar looks at them.
/// </summary>
public enum PlatformMenuRole
{
    /// <summary>An item that does what <see cref="PlatformMenuItem.Picked"/> says, or a menu with only what it holds.</summary>
    None,

    /// <summary>The program's About window. Its <see cref="PlatformMenuItem.Picked"/> shows it.</summary>
    About,

    /// <summary>The program's settings (Cmd+, on a Mac). Its <see cref="PlatformMenuItem.Picked"/> shows them.</summary>
    Settings,

    /// <summary>The system's Services submenu, which the system fills.</summary>
    Services,

    /// <summary>Hides the program (Cmd+H). The system's action.</summary>
    Hide,

    /// <summary>Hides every other program (Option+Cmd+H). The system's action.</summary>
    HideOthers,

    /// <summary>Shows every program that was hidden. The system's action.</summary>
    ShowAll,

    /// <summary>
    /// Quits (Cmd+Q). The system's action, which comes to the program as its window being asked to close, with
    /// <see cref="IPlatformHost.CloseIsQuit"/> saying it was the application and not just the window.
    /// </summary>
    Quit,

    /// <summary>Minimises the window (Cmd+M). The system's action.</summary>
    Minimize,

    /// <summary>Zooms the window. The system's action.</summary>
    Zoom,

    /// <summary>Takes the window in and out of the system's full screen (Ctrl+Cmd+F). The system's action.</summary>
    FullScreen,

    /// <summary>Brings the program's windows in front of the others. The system's action.</summary>
    BringAllToFront,

    /// <summary>On a menu: the system lists the program's windows in it (a Window menu).</summary>
    WindowList,

    /// <summary>On a menu: the system adds its search field to it (a Help menu).</summary>
    HelpSearch,
}

/// <summary>
/// One line of a <see cref="PlatformMenu"/> for the platform to put in its own menu bar: something to do, a rule between
/// two groups of items, or a menu of its own that opens to the side. The state (<see cref="Enabled"/>,
/// <see cref="Checked"/>, <see cref="Visible"/>) is asked when the platform needs it (as a menu opens), never every frame.
/// </summary>
public sealed class PlatformMenuItem
{
    /// <summary>What the item says. Empty for a rule.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// The key as the program writes keys ("Ctrl+Shift+P", "Ctrl+K Z" for a chord, one key and then the next), empty for
    /// none. Ctrl is the system's usual modifier, Command on a Mac. The platform shows it; whether the key picks the
    /// item is the program's own business, see <see cref="IPlatformMenuBar"/>.
    /// </summary>
    public string Shortcut { get; init; } = string.Empty;

    /// <summary>What the item stands for when the system has an action of its own for it.</summary>
    public PlatformMenuRole Role { get; init; }

    /// <summary>The menu the item opens, null for one that does something itself.</summary>
    public PlatformMenu? Submenu { get; init; }

    /// <summary>Whether this is a rule between two groups of items.</summary>
    public bool IsSeparator { get; init; }

    /// <summary>Whether the item can be picked right now. Always when null.</summary>
    public Func<bool>? Enabled { get; init; }

    /// <summary>Whether the item has a tick in front of it right now. Never when null.</summary>
    public Func<bool>? Checked { get; init; }

    /// <summary>Whether the item is there at all right now. Always when null.</summary>
    public Func<bool>? Visible { get; init; }

    /// <summary>What picking the item does, which the platform has run on the program's own thread (see <see cref="IPlatformMenuBar.RunPicked"/>).</summary>
    public Action? Picked { get; init; }

    /// <summary>A rule.</summary>
    public static PlatformMenuItem Separator() => new() { IsSeparator = true };

    /// <summary>Whether the item can be picked right now.</summary>
    public bool IsEnabled() => !IsSeparator && (Enabled?.Invoke() ?? true);

    /// <summary>Whether the item is ticked right now.</summary>
    public bool IsChecked() => Checked?.Invoke() ?? false;

    /// <summary>Whether the item is there right now.</summary>
    public bool IsVisible() => Visible?.Invoke() ?? true;
}

/// <summary>
/// A menu for the platform's own menu bar: one of the words on the bar and what opens under it, or a menu inside another.
/// Its items can be given as a list, or as a function the platform calls whenever the menu is about to open, for one
/// whose items change as the program does (recent files, what only means something some of the time).
/// </summary>
public sealed class PlatformMenu
{
    private readonly IReadOnlyList<PlatformMenuItem> items;
    private readonly Func<IReadOnlyList<PlatformMenuItem>>? source;

    /// <summary>A menu with the items it has.</summary>
    public PlatformMenu(string title, IReadOnlyList<PlatformMenuItem>? items = null, PlatformMenuRole role = PlatformMenuRole.None)
    {
        Title = title;
        this.items = items ?? [];
        Role = role;
    }

    /// <summary>A menu whose items are asked for each time it is about to open.</summary>
    public PlatformMenu(string title, Func<IReadOnlyList<PlatformMenuItem>> source, PlatformMenuRole role = PlatformMenuRole.None)
        : this(title, (IReadOnlyList<PlatformMenuItem>?)null, role)
    {
        this.source = source;
    }

    /// <summary>The word on the bar, or what the item that opens it says.</summary>
    public string Title { get; }

    /// <summary><see cref="PlatformMenuRole.WindowList"/> or <see cref="PlatformMenuRole.HelpSearch"/> for a menu the system adds to, none for any other.</summary>
    public PlatformMenuRole Role { get; }

    /// <summary>Whether the menu is on the bar right now. Always when null.</summary>
    public Func<bool>? Visible { get; init; }

    /// <summary>The items of the menu as of now.</summary>
    public IReadOnlyList<PlatformMenuItem> Items => source?.Invoke() ?? items;

    /// <summary>Whether <see cref="Items"/> can be different the next time it's asked.</summary>
    public bool IsDynamic => source is not null;

    /// <summary>Whether the menu is on the bar right now.</summary>
    public bool IsVisible() => Visible?.Invoke() ?? true;
}

/// <summary>
/// The platform's own menu bar, which the desktop host has on a Mac (<see cref="IPlatformHost.MenuBar"/>) and not
/// elsewhere. The program gives it menus (<see cref="PlatformMenu"/>) put together from its own, and what the platform
/// draws is a mirror of them: the program's own key handling stays the source of truth, so a key the menu shows is
/// still heard by the program as it is without the menu and never picks an item twice. Mouse picks come to
/// <see cref="PlatformMenuItem.Picked"/> by way of <see cref="RunPicked"/>.
/// </summary>
public interface IPlatformMenuBar
{
    /// <summary>Whether the platform's own bar is there and has taken over (the program leaves its own bar out when it has).</summary>
    bool IsNative { get; }

    /// <summary>
    /// What runs the action of a pick on the program's own thread, which the program sets (the platform's thread is another
    /// one). Left null, a pick runs on the platform's thread.
    /// </summary>
    Action<Action>? RunPicked { get; set; }

    /// <summary>Puts these menus on the bar, in this order, in place of whatever was there. From any thread.</summary>
    void SetMenus(IReadOnlyList<PlatformMenu> menus);

    /// <summary>Has the bar look at the menus' state again (which menus are on it, titles), for when it changed without a menu opening. From any thread.</summary>
    void Invalidate();
}
