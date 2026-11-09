using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// Where a command's key goes off, the menu it was added to when that menu is only on the bar some of the time, and what
/// its key was added to do. What a list of keys goes by to tell two commands that fight over a key from two that don't.
/// </summary>
public class UICommandsContextTests
{
    private static UICommands Declared(Func<bool> designing)
    {
        var commands = new UICommands();
        commands.Declare("Go", "open");
        commands.Declare("Layout", "group");
        commands.ShowMenuWhen("Layout", designing);
        return commands;
    }

    [Fact]
    public void A_command_of_a_menu_that_comes_and_goes_is_in_that_menu_s_place()
    {
        var commands = Declared(() => true);
        MenuItem group = commands.Add("Layout/group", "Group", () => { }, "Ctrl+G", use: UIKeyUse.Bound);
        MenuItem line = commands.Add("Go/open", "Go to Line", () => { }, "Ctrl+G");

        Assert.Equal("Layout", commands.ContextOf(group));
        Assert.Null(commands.ContextOf(line));
    }

    [Fact]
    public void What_is_filled_into_a_menu_of_its_own_is_in_the_place_of_the_menu_it_was_added_to()
    {
        bool designing = false;
        var commands = Declared(() => designing);
        Menu convert = commands.AddMenu("Layout/group", "Convert To");
        MenuItem group = convert.Add("Group", () => { });
        Menu deeper = convert.AddMenu("More");
        MenuItem grid = deeper.Add("Grid", () => { });
        MenuItem line = commands.AddMenu("Go/open", "Recent").Add("app.d", () => { });

        Assert.Equal("Layout", commands.ContextOf(group));
        Assert.Equal("Layout", commands.ContextOf(grid));
        Assert.Null(commands.ContextOf(line));
        Assert.False(commands.CanPick(group));
        Assert.True(commands.CanPick(line));

        designing = true;
        Assert.True(commands.CanPick(group));
    }

    [Fact]
    public void What_a_key_was_added_to_do_is_kept()
    {
        var commands = Declared(() => true);
        MenuItem shown = commands.Add("Go/open", "Go to Line", () => { }, "Ctrl+G");
        MenuItem bound = commands.Add("Layout/group", "Group", () => { }, "Ctrl+G", use: UIKeyUse.Bound);
        MenuItem typing = commands.Add("Go/open", "Go to File", () => { }, "Ctrl+P", use: UIKeyUse.BoundWhileTyping);

        Assert.Equal(UIKeyUse.Shown, commands.UseOf(shown));
        Assert.Equal(UIKeyUse.Bound, commands.UseOf(bound));
        Assert.Equal(UIKeyUse.BoundWhileTyping, commands.UseOf(typing));
    }

    [Fact]
    public void An_item_that_wasnt_added_here_is_nowhere_in_particular()
    {
        var commands = Declared(() => true);
        var stranger = new MenuItem { Label = "Elsewhere", OnPicked = () => { } };

        Assert.Null(commands.ContextOf(stranger));
        Assert.Equal(UIKeyUse.Shown, commands.UseOf(stranger));
        Assert.True(commands.CanPick(stranger));
    }

    [Fact]
    public void A_command_can_be_picked_only_while_its_menu_is_on_the_bar_and_it_isnt_greyed_out()
    {
        bool designing = false, some = true;
        var commands = Declared(() => designing);
        MenuItem group = commands.Add("Layout/group", "Group", () => { }, "Ctrl+G", use: UIKeyUse.Bound);
        group.IsEnabled = () => some;
        MenuItem line = commands.Add("Go/open", "Go to Line", () => { }, "Ctrl+G");

        Assert.False(commands.CanPick(group));
        Assert.True(commands.CanPick(line));

        designing = true;
        Assert.True(commands.CanPick(group));

        some = false;
        Assert.False(commands.CanPick(group));
    }
}
