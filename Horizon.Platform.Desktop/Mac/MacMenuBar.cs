using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Horizon.Platform.Desktop;

/// <summary>
/// A Mac's menu bar, an NSMenu that mirrors the <see cref="PlatformMenu"/>s a program gives it. It replaces the one SDL
/// makes (About, Hide, Quit and Window and nothing else) as the application's main menu.
/// <para>
/// Whose keys they are: the program's own key handling is what answers a key, as it does without a menu (chords, keys the
/// user moved, keys that mean something else while typing). The items show a key equivalent
/// (<see cref="MacKeyEquivalents"/>) and <c>validateMenuItem:</c> says no to one of them while AppKit is matching a key
/// press to it (the current event is a key down and no menu is open), so the key goes on to the window and the program
/// as if there were no menu. A pick with the mouse is the only thing that runs <see cref="PlatformMenuItem.Picked"/>, on the
/// thread <see cref="RunPicked"/> says. The items of a <see cref="PlatformMenuRole"/> the system has an action for (Hide,
/// Quit, Minimize, the Services and Window lists) are the system's own, with its own keys.
/// </para>
/// <para>
/// A menu is filled again, from the program's own, as it is about to open (<c>menuNeedsUpdate:</c>), never every frame.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacMenuBar : IPlatformMenuBar
{
    private sealed class MenuNode(nint menu, PlatformMenu source)
    {
        public readonly nint Menu = menu;
        public readonly PlatformMenu Source = source;
        public readonly List<ItemNode> Items = [];
    }

    private sealed class ItemNode(nint item, PlatformMenuItem source, bool system, bool hasKey)
    {
        public readonly nint Item = item;
        public readonly PlatformMenuItem Source = source;
        public readonly bool System = system;
        public readonly bool HasKey = hasKey;
        public MenuNode? Submenu;
    }

    // NSEventTypeKeyDown
    private const nuint KEY_DOWN = 10;

    // What the system does for each role, its action (nil target: the first responder that has it, the application or the window), its key
    private static (string Action, string Key, ulong Mask)? SystemAction(PlatformMenuRole role) => role switch
    {
        PlatformMenuRole.Hide => ("hide:", "h", MacKeyEquivalents.Command),
        PlatformMenuRole.HideOthers => ("hideOtherApplications:", "h", MacKeyEquivalents.Command | MacKeyEquivalents.Option),
        PlatformMenuRole.ShowAll => ("unhideAllApplications:", "", 0),
        PlatformMenuRole.Quit => ("terminate:", "q", MacKeyEquivalents.Command),
        PlatformMenuRole.Minimize => ("performMiniaturize:", "m", MacKeyEquivalents.Command),
        PlatformMenuRole.Zoom => ("performZoom:", "", 0),
        PlatformMenuRole.FullScreen => ("toggleFullScreen:", "f", MacKeyEquivalents.Command | MacKeyEquivalents.Control),
        PlatformMenuRole.BringAllToFront => ("arrangeInFront:", "", 0),
        _ => null,
    };

    // There is one main menu in a process, so there is one of these and what AppKit calls goes to it
    private static MacMenuBar? current;
    private static nint targetClass, target;

    private readonly Dictionary<nint, MenuNode> menusByPointer = [];
    private readonly Dictionary<nint, ItemNode> itemsByPointer = [];
    private readonly List<(nint Item, PlatformMenu Source)> topLevel = [];
    private readonly HashSet<nint> openMenus = [];
    private nint mainMenu;
    private nint helpMenuHandle;
    private IReadOnlyList<PlatformMenu> wanted = [];
    private volatile bool installed;

    public bool IsNative => installed;

    public Action<Action>? RunPicked { get; set; }

    /// <summary>The titles of the menus on the bar as last made, for a log.</summary>
    public string Describe() => string.Join(" | ", topLevel.Select(top => top.Source.Title));

    public void SetMenus(IReadOnlyList<PlatformMenu> menus)
    {
        ArgumentNullException.ThrowIfNull(menus);
        IReadOnlyList<PlatformMenu> taken = [.. menus];
        MacMain.Run(() => Install(taken));
    }

    public void Invalidate() => MacMain.Run(Refresh);

    /* Making the bar. Main thread */

    private void Install(IReadOnlyList<PlatformMenu> menus)
    {
        nint newBar = 0;
        try
        {
            EnsureTarget();
            current = this;
            wanted = menus;
            Forget();

            nint application = MacObjC.SharedApplication;
            newBar = MacObjC.Send(MacObjC.Send(MacObjC.Class("NSMenu"), MacObjC.Sel("alloc")), MacObjC.Sel("initWithTitle:"), MacObjC.NSString(string.Empty));
            nint windows = 0, help = 0;

            for (int i = 0; i < menus.Count; i++)
            {
                PlatformMenu source = menus[i];
                MenuNode node = BuildMenu(source);

                nint top = NewItem(source.Title, 0, string.Empty, 0);
                MacObjC.SendVoid(top, MacObjC.Sel("setSubmenu:"), node.Menu);
                MacObjC.SendVoid(newBar, MacObjC.Sel("addItem:"), top);
                if (i > 0 && !SafeVisible(source))
                    MacObjC.SendVoid(top, MacObjC.Sel("setHidden:"), true);

                topLevel.Add((top, source));
                MacObjC.Release(top);
                MacObjC.Release(node.Menu);

                if (source.Role == PlatformMenuRole.WindowList)
                    windows = node.Menu;
                else if (source.Role == PlatformMenuRole.HelpSearch)
                    help = node.Menu;
            }

            // The system's menu is gone from here on, SDL's with it
            MacObjC.SendVoid(application, MacObjC.Sel("setMainMenu:"), newBar);
            if (windows != 0)
                MacObjC.SendVoid(application, MacObjC.Sel("setWindowsMenu:"), windows);
            if (help != 0)
                MacObjC.SendVoid(application, MacObjC.Sel("setHelpMenu:"), help);

            helpMenuHandle = help;
            MacObjC.Release(mainMenu);
            mainMenu = newBar;
            installed = true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            if (newBar != 0 && newBar != mainMenu)
                MacObjC.Release(newBar);

            // No menu of the system's is a program that has its own bar still, which the program sees as IsNative staying false
            Console.Error.WriteLine($"[Horizon] the native menu bar wasn't made, {e.Message}");
        }
    }

    // Looking at the state of the bar again, which menus are there
    private void Refresh()
    {
        if (!installed)
            return;

        for (int i = 1; i < topLevel.Count; i++)
        {
            var (item, source) = topLevel[i];
            bool hidden = MacObjC.SendBool(item, MacObjC.Sel("isHidden"));
            bool visible = SafeVisible(source);
            if (hidden == visible)
                MacObjC.SendVoid(item, MacObjC.Sel("setHidden:"), !visible);
        }
    }

    private static bool SafeVisible(PlatformMenu source)
    {
        try { return source.IsVisible(); }
        catch (Exception) { return true; }
    }

    private void Forget()
    {
        menusByPointer.Clear();
        itemsByPointer.Clear();
        topLevel.Clear();
        openMenus.Clear();
        helpMenuHandle = 0;
    }

    private static void EnsureTarget()
    {
        if (target != 0)
            return;

        DisableAutoEditItems();

        // The item's target and the menus' delegate, one object. A class of its own, the program's names for what AppKit asks
        var builder = new MacClassBuilder("HorizonMenuTarget")
            .AddMethod("pick:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Pick, "v@:@")
            .AddMethod("validateMenuItem:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&ValidateMenuItem, "c@:@")
            .AddMethod("menuNeedsUpdate:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&MenuNeedsUpdate, "v@:@")
            .AddMethod("menuWillOpen:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&MenuWillOpen, "v@:@")
            .AddMethod("menuDidClose:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&MenuDidClose, "v@:@")
            .AddProtocol("NSMenuDelegate")
            .AddProtocol("NSMenuItemValidation");
        targetClass = builder.Register();
        target = MacObjC.New(targetClass);
    }

    // AppKit auto-injects "Start Dictation…" and "Emoji & Symbols" into any menu titled "Edit", which duplicates them
    // when a dynamic Edit menu is refilled on open. Registering these defaults in memory suppresses that without writing
    // to the user's persisted preferences.
    private static void DisableAutoEditItems()
    {
        nint defaults = MacObjC.Send(MacObjC.Class("NSUserDefaults"), MacObjC.Sel("standardUserDefaults"));
        if (defaults == 0)
            return;

        nint dict = MacObjC.Send(MacObjC.Class("NSMutableDictionary"), MacObjC.Sel("dictionary"));
        nint yes = MacObjC.Send(MacObjC.Class("NSNumber"), MacObjC.Sel("numberWithBool:"), (nint)1);
        MacObjC.SendVoid(dict, MacObjC.Sel("setObject:forKey:"), yes, MacObjC.NSString("NSDisabledDictationMenuItem"));
        MacObjC.SendVoid(dict, MacObjC.Sel("setObject:forKey:"), yes, MacObjC.NSString("NSDisabledCharacterPaletteMenuItem"));
        MacObjC.SendVoid(defaults, MacObjC.Sel("registerDefaults:"), dict);
    }

    // A menu with its items made, a new one the caller owns. Nothing of the program's is kept but its menu and items
    private MenuNode BuildMenu(PlatformMenu source)
    {
        nint menu = MacObjC.Send(MacObjC.Send(MacObjC.Class("NSMenu"), MacObjC.Sel("alloc")), MacObjC.Sel("initWithTitle:"), MacObjC.NSString(source.Title));
        MacObjC.SendVoid(menu, MacObjC.Sel("setDelegate:"), target);
        var node = new MenuNode(menu, source);
        menusByPointer[menu] = node;
        Fill(node);
        return node;
    }

    // Puts the items of a menu in it as the program has them now
    private void Fill(MenuNode node)
    {
        IReadOnlyList<PlatformMenuItem> items;
        try
        {
            items = node.Source.Items;
        }
        catch (Exception e)
        {
            // A menu the program couldn't put together (it changed as it was read) is empty until it's asked again as it opens
            Console.Error.WriteLine($"[Horizon] the menu {node.Source.Title} couldn't be read, {e.Message}");
            return;
        }

        foreach (PlatformMenuItem source in items)
        {
            if (source.IsSeparator)
            {
                MacObjC.SendVoid(node.Menu, MacObjC.Sel("addItem:"), MacObjC.Send(MacObjC.Class("NSMenuItem"), MacObjC.Sel("separatorItem")));
                continue;
            }

            if (!SafeVisibleItem(source))
                continue;

            ItemNode made = BuildItem(source);
            node.Items.Add(made);
            itemsByPointer[made.Item] = made;
            MacObjC.SendVoid(node.Menu, MacObjC.Sel("addItem:"), made.Item);
            MacObjC.Release(made.Item);
        }
    }

    private static bool SafeVisibleItem(PlatformMenuItem source)
    {
        try { return source.IsVisible(); }
        catch (Exception) { return true; }
    }

    // A new item the caller owns
    private static nint NewItem(string title, nint action, string key, ulong mask)
    {
        nint item = MacObjC.Send(MacObjC.Send(MacObjC.Class("NSMenuItem"), MacObjC.Sel("alloc")), MacObjC.Sel("initWithTitle:action:keyEquivalent:"),
            MacObjC.NSString(title), action, MacObjC.NSString(key));
        // AppKit defaults keyEquivalentModifierMask to Command, so set it even when mask == 0 for modifierless keys (F5, Esc, Delete)
        if (key.Length > 0)
            MacObjC.SendVoid(item, MacObjC.Sel("setKeyEquivalentModifierMask:"), (nuint)mask);
        return item;
    }

    private ItemNode BuildItem(PlatformMenuItem source)
    {
        // What the system does by itself, with the system's own key
        if (SystemAction(source.Role) is { } system)
        {
            nint item = NewItem(source.Label, MacObjC.Sel(system.Action), system.Key, system.Mask);
            return new ItemNode(item, source, system: true, hasKey: system.Key.Length > 0);
        }

        if (source.Role == PlatformMenuRole.Services)
        {
            nint item = NewItem(source.Label, 0, string.Empty, 0);
            nint services = MacObjC.Send(MacObjC.Send(MacObjC.Class("NSMenu"), MacObjC.Sel("alloc")), MacObjC.Sel("initWithTitle:"), MacObjC.NSString(source.Label));
            MacObjC.SendVoid(item, MacObjC.Sel("setSubmenu:"), services);
            MacObjC.SendVoid(MacObjC.SharedApplication, MacObjC.Sel("setServicesMenu:"), services);
            MacObjC.Release(services);
            return new ItemNode(item, source, system: true, hasKey: false);
        }

        if (source.Submenu is { } submenu)
        {
            nint item = NewItem(source.Label, 0, string.Empty, 0);
            MenuNode inside = BuildMenu(submenu);
            MacObjC.SendVoid(item, MacObjC.Sel("setSubmenu:"), inside.Menu);
            MacObjC.Release(inside.Menu);
            return new ItemNode(item, source, system: false, hasKey: false) { Submenu = inside };
        }

        // An item of the program's, its key shown and the program's own to answer
        MacKeyEquivalent? equivalent = MacKeyEquivalents.Map(source.Shortcut);
        string title = source.Label;
        if (equivalent is null && MacKeyEquivalents.IsChord(source.Shortcut))
            title = $"{title}  {MacKeyEquivalents.Glyphs(source.Shortcut)}";

        nint picked = NewItem(title, MacObjC.Sel("pick:"), equivalent?.Key ?? string.Empty, equivalent?.Modifiers ?? 0);
        MacObjC.SendVoid(picked, MacObjC.Sel("setTarget:"), target);
        if (SafeChecked(source))
            MacObjC.SendVoid(picked, MacObjC.Sel("setState:"), (long)1);

        return new ItemNode(picked, source, system: false, hasKey: equivalent is not null);
    }

    private static bool SafeChecked(PlatformMenuItem source)
    {
        try { return source.IsChecked(); }
        catch (Exception) { return false; }
    }

    // Takes the items out of a menu that is about to open and puts the ones the program has now in, forgetting what was in it
    private void Refill(MenuNode node)
    {
        foreach (ItemNode item in node.Items)
            Forget(item);
        node.Items.Clear();

        MacObjC.SendVoid(node.Menu, MacObjC.Sel("removeAllItems"));
        Fill(node);
    }

    private void Forget(ItemNode item)
    {
        itemsByPointer.Remove(item.Item);
        if (item.Submenu is { } inside)
        {
            menusByPointer.Remove(inside.Menu);
            openMenus.Remove(inside.Menu);
            foreach (ItemNode deeper in inside.Items)
                Forget(deeper);
        }
    }

    // Whether the current Cocoa event is a key-down press (independent of whether any menu is open).
    private static bool CurrentEventIsKeyDown()
    {
        nint ev = MacObjC.Send(MacObjC.SharedApplication, MacObjC.Sel("currentEvent"));
        return ev != 0 && MacObjC.SendUInt(ev, MacObjC.Sel("type")) == KEY_DOWN;
    }

    // Whether AppKit is only matching a key press to an item and not showing a menu, which is when the program's keys answer
    private bool MatchingKey() => openMenus.Count == 0 && CurrentEventIsKeyDown();

    private bool IsMenuOrSubtreeOpen(MenuNode node)
    {
        if (openMenus.Contains(node.Menu))
            return true;

        foreach (ItemNode item in node.Items)
        {
            if (item.Submenu is { } child && IsMenuOrSubtreeOpen(child))
                return true;
        }
        return false;
    }

    /* What AppKit calls. Main thread, and nothing is let out of them */

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Pick(nint self, nint command, nint sender)
    {
        try
        {
            using var pool = MacObjC.Pool();
            if (current is not { } bar || !bar.itemsByPointer.TryGetValue(sender, out ItemNode? node) || node.Source.Picked is not { } picked)
                return;

            if (bar.RunPicked is { } run)
                run(picked);
            else
                picked();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Horizon] a menu pick failed, {e.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ValidateMenuItem(nint self, nint command, nint item)
    {
        try
        {
            if (current is not { } bar || !bar.itemsByPointer.TryGetValue(item, out ItemNode? node))
                return 1;

            bool enabled = node.Source.IsEnabled();

            // The state shows as the menu opens, which is when this is asked
            MacObjC.SendVoid(item, MacObjC.Sel("setState:"), (long)(SafeChecked(node.Source) ? 1 : 0));

            // A key AppKit is matching to the item is the program's to answer: saying no lets it go on to the window
            if (enabled && node.HasKey && !node.System && bar.MatchingKey())
                return 0;

            return enabled ? (byte)1 : (byte)0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuNeedsUpdate(nint self, nint command, nint menu)
    {
        try
        {
            using var pool = MacObjC.Pool();
            if (current is not { } bar || !bar.menusByPointer.TryGetValue(menu, out MenuNode? node))
                return;

            // The system adds to a Window or Help menu itself; never clear an open menu or open Help-search subtree mid-event.
            bool helpOpen = bar.helpMenuHandle != 0 && bar.openMenus.Contains(bar.helpMenuHandle);
            if (MacMenuBarRules.ShouldRefillMenu(node.Source.IsDynamic, node.Source.Role, CurrentEventIsKeyDown(), bar.IsMenuOrSubtreeOpen(node), helpOpen))
                bar.Refill(node);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Horizon] a menu couldn't be updated, {e.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuWillOpen(nint self, nint command, nint menu)
    {
        if (current is { } bar && menu != 0)
            bar.openMenus.Add(menu);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuDidClose(nint self, nint command, nint menu)
    {
        if (current is { } bar && menu != 0)
            bar.openMenus.Remove(menu);
    }
}

/// <summary>Pure decision rules for <see cref="MacMenuBar"/> updates, testable independently of AppKit.</summary>
internal static class MacMenuBarRules
{
    internal static bool ShouldRefillMenu(bool isDynamic, PlatformMenuRole role, bool isKeyDown, bool menuOrSubtreeOpen, bool helpMenuOpen) =>
        isDynamic && role == PlatformMenuRole.None && !isKeyDown && !menuOrSubtreeOpen && !helpMenuOpen;
}
