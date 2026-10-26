using Horizon.UI;
using Horizon.UI.Components;

using Silk.NET.Input;

namespace Horizon.Tests.UI;

/// <summary>
/// Keys written the way the menus write them, the shortcuts that go off on them, and the menus put together out of the
/// commands each part of a program adds of its own.
/// </summary>
public class UICommandsTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F", Key.F, true, true, false)]
    [InlineData("ctrl + alt + b", Key.B, true, false, true)]
    [InlineData("Shift+F12", Key.F12, false, true, false)]
    [InlineData("Ctrl+\\", Key.BackSlash, true, false, false)]
    [InlineData("Ctrl+,", Key.Comma, true, false, false)]
    [InlineData("Del", Key.Delete, false, false, false)]
    [InlineData("Cmd+1", Key.Number1, true, false, false)]
    public void A_key_reads_the_way_the_menus_write_it(string written, Key key, bool control, bool shift, bool alt)
    {
        Assert.Equal((key, control, shift, alt), UIKeys.Read(written));
    }

    [Theory]
    [InlineData("Ctrl+Nope")]
    [InlineData("Ctrl+F+G")]
    [InlineData("12")]
    [InlineData("")]
    public void What_isnt_a_key_doesnt_read(string written) => Assert.Null(UIKeys.Read(written));

    [Fact]
    public void A_key_is_written_back_the_same_however_it_was_written()
    {
        Assert.Equal("Ctrl+Shift+Alt+F", UIKeys.Tidy("alt+shift+ctrl+f"));
        Assert.Equal("Ctrl+=", UIKeys.Tidy("control+Equal"));
        Assert.Equal("Del", UIKeys.Tidy("delete"));
    }

    [Fact]
    public void A_chord_is_two_keys_with_a_gap_between()
    {
        Assert.Equal((new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.S, true, false, false)), UIKeys.ReadChord("Ctrl+K Ctrl+S"));
        Assert.Equal((new UIKeyStroke(Key.K, true, false, false), new UIKeyStroke(Key.Z, false, false, false)), UIKeys.ReadChord("ctrl+k  z"));
        Assert.Equal((new UIKeyStroke(Key.B, true, false, true), (UIKeyStroke?)null), UIKeys.ReadChord("ctrl + alt + b"));
        Assert.Equal("Ctrl+K Z", UIKeys.Tidy("control+k z"));
        Assert.Equal("Ctrl+K", UIKeys.FirstOf("Ctrl+K Ctrl+C"));
        Assert.True(UIKeys.IsChord("Ctrl+K Ctrl+C"));
        Assert.False(UIKeys.IsChord("Ctrl+K"));

        // One key reads as ever, a chord isn't one key, and three keys or a key that isn't aren't a chord
        Assert.Null(UIKeys.Read("Ctrl+K Z"));
        Assert.Null(UIKeys.ReadChord("Ctrl+K Z X"));
        Assert.Null(UIKeys.ReadChord("Ctrl+K Nope"));
        Assert.Equal("Ctrl+K Ctrl+S", UIShortcutText.Shown("Ctrl+K Ctrl+S").Replace("Cmd", "Ctrl"));
    }

    [Fact]
    public void A_key_without_shift_leaves_shift_to_the_same_key_with_it_when_that_is_something_else()
    {
        var shortcuts = new UIShortcuts();
        shortcuts.Add(Key.S, () => { }, control: true);
        shortcuts.Add(Key.Z, () => { }, control: true);
        shortcuts.Add(Key.S, () => { }, control: true, shift: true);

        // Ctrl+S and Ctrl+Shift+S are two things, Ctrl+Z goes off with shift held as well
        Assert.True(shortcuts.Has(Key.S, control: true, shift: false, alt: false));
        Assert.True(shortcuts.Has(Key.S, control: true, shift: true, alt: false));
        Assert.NotNull(shortcuts.Remove(Key.S, control: true, shift: true, alt: false));
        Assert.True(shortcuts.Has(Key.S, control: true, shift: false, alt: false));
        Assert.True(shortcuts.Has(Key.Z, control: true, shift: true, alt: false));
    }

    [Fact]
    public void The_menus_are_their_groups_in_order_with_a_rule_between_two_that_have_something()
    {
        var commands = new UICommands();
        commands.Declare("File", "new", "save", "exit");
        commands.Declare("Help", "help");

        // Added out of order, they go where their group is
        commands.Add("File/exit", "Exit", () => { });
        commands.Add("File/new", "New", () => { }, "Ctrl+N", use: UIKeyUse.BoundWhileTyping);
        commands.Add("File/new", "Open...", () => { }, "Ctrl+O");

        var bar = new MenuBar();
        commands.Build(bar);

        Menu file = Assert.Single(bar.Menus, menu => menu.Title == "File");
        Assert.Equal(["New", "Open...", "", "Exit"], file.Items.Select(item => item.Label));
        Assert.True(file.Items[2].IsSeparator);
        Assert.Empty(Assert.Single(bar.Menus, menu => menu.Title == "Help").Items);

        // Bound, the key goes off, only shown, it's somebody else's
        Assert.True(commands.Keys.Has(Key.N, control: true, shift: false, alt: false));
        Assert.False(commands.Keys.Has(Key.O, control: true, shift: false, alt: false));
    }

    [Fact]
    public void A_menu_of_its_own_takes_commands_in_its_groups_and_goes_when_nothing_in_it_is_there()
    {
        var commands = new UICommands();
        commands.Declare("File", "save", "preferences", "exit");
        commands.ShowMenuWhen("File", () => true);
        Menu prefs = commands.DeclareSubmenu("File/preferences", "prefs", "Preferences", "", "main", "files");

        bool keysShown = true;
        commands.Add("File/preferences/prefs:files", "Open Settings File", () => { });
        MenuItem keys = commands.Add("File/preferences/prefs:main", "Keyboard Shortcuts", () => { }, "Ctrl+K Ctrl+S", use: UIKeyUse.Bound);
        keys.IsVisible = () => keysShown;
        commands.Add("File/save", "Save", () => { });

        var bar = new MenuBar();
        commands.Build(bar);

        Menu file = Assert.Single(bar.Menus, menu => menu.Title == "File");
        Assert.Equal(["Save", "", "Preferences"], file.Items.Select(item => item.Label));
        Assert.Same(prefs, file.Items[2].Submenu);
        Assert.Equal(["Keyboard Shortcuts", "", "Open Settings File"], prefs.Items.Select(item => item.Label));

        // Its commands are the bar menu's, a chord inside it goes off and says where it means something
        Assert.Equal("File", commands.ContextOf(keys));
        Assert.Equal(UIKeyUse.Bound, commands.UseOf(keys));

        // A group of it with nothing shown has no rule left over, and with nothing at all the item that opens it goes
        keysShown = false;
        Assert.False(prefs.IsShown(1));
        Assert.True(file.Items[2].IsVisible!());
        Assert.Throws<ArgumentException>(() => commands.Add("File/preferences/nope", "Nope", () => { }));
        Assert.Throws<ArgumentException>(() => commands.Add("File/preferences/prefs:nope", "Nope", () => { }));
    }

    [Fact]
    public void A_command_for_the_palette_only_is_not_drawn_but_keeps_its_key()
    {
        var commands = new UICommands();
        commands.Declare("File", "save", "path");
        commands.Add("File/save", "Save", () => { });
        bool copied = false;
        MenuItem copy = commands.Add("File/path", "Copy Path", () => copied = true, "Shift+Alt+C", use: UIKeyUse.Bound);
        copy.PaletteOnly = true;

        var bar = new MenuBar();
        commands.Build(bar);

        // Not drawn, and the rule before it goes with it, but it's still one of the menu's commands
        Menu file = Assert.Single(bar.Menus);
        int at = file.Items.ToList().IndexOf(copy);
        Assert.False(file.IsShown(at));
        Assert.False(file.IsShown(at - 1));
        Assert.Contains(file.Everything(), pair => pair.Item == copy);

        // And its key still goes off
        Assert.True(commands.Keys.Has(Key.C, control: false, shift: true, alt: true));
        Assert.Equal(UIKeyUse.Bound, commands.UseOf(copy));
        copy.OnPicked!();
        Assert.True(copied);
    }

    [Fact]
    public void Two_rules_never_show_in_a_row_or_at_an_end()
    {
        var menu = new Menu("Edit");
        menu.AddSeparator();
        menu.Add("Undo", () => { });
        menu.AddSeparator();
        menu.Add("Paste", () => { }).IsVisible = () => false;
        menu.AddSeparator();
        menu.Add("Find", () => { });
        menu.AddSeparator();
        menu.Add("Replace", () => { }).IsVisible = () => false;

        // Undo, a rule, (Paste), no second rule, Find, and no rule at the end
        Assert.Equal([true, true, false, false, true, false, false], Enumerable.Range(0, menu.Items.Count).Select(menu.IsShown));
        Assert.True(menu.HasShown());
    }

    [Fact]
    public void A_group_nobody_declared_is_said_as_the_part_adds_to_it()
    {
        var commands = new UICommands();
        commands.Declare("Edit", "find");

        Assert.Throws<ArgumentException>(() => commands.Add("Edit/nope", "Find", () => { }));
        Assert.Throws<ArgumentException>(() => commands.Add("View/find", "Find", () => { }));
        Assert.Throws<ArgumentException>(() => commands.Add("Edit/find", "Find", () => { }, "Ctrl+Nope", use: UIKeyUse.Bound));
    }
}
