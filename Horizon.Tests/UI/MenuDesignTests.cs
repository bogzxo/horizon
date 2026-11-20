using System.Numerics;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

// How a menu's list is laid out and painted, a width it can be read across, and an item that throws something away
// told apart from the rest before it's picked
public class MenuDesignTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private static readonly UIRect Frame = new(new Vector2(-2000), new Vector2(2000));

    private static MenuPanel Panel(Menu menu) => new(menu, Skin, 0, MenuPlacement.AtPoint, new UIRect(Vector2.Zero, Vector2.Zero), Frame);

    private static SpriteItem[] Paint(MenuPanel panel)
    {
        // Twice, a second apart, the second after its entrance has faded it in
        var list = new UIDrawList();
        for (int clock = 1; clock <= 2; clock++)
        {
            list.Begin(Skin, dt: 1f / 120, clock: clock);
            panel.Paint(list, new Vector2(float.NegativeInfinity));
            list.End();
        }
        return list.Items.ToArray();
    }

    [Fact]
    public void Adding_an_item_can_say_it_is_destructive()
    {
        var menu = new Menu("file");
        Assert.False(menu.Add("Open", () => { }).IsDestructive);
        Assert.True(menu.Add("Delete", () => { }, "Del", "trash", destructive: true).IsDestructive);

        var commands = new UICommands();
        commands.Declare("File", "save");
        Assert.True(commands.Add("File/save", "Revert File", () => { }, icon: "discard", destructive: true).IsDestructive);
    }

    [Fact]
    public void A_list_of_one_short_item_is_still_wide_enough_to_aim_at()
    {
        var menu = new Menu("tiny");
        menu.Add("Go", () => { });
        MenuPanel panel = Panel(menu);
        Assert.True(panel.Bounds.Width >= panel.MinWidth(Skin), $"{panel.Bounds.Width} is narrower than {panel.MinWidth(Skin)}");
    }

    [Fact]
    public void A_list_with_a_long_item_is_no_wider_than_it_can_be_read_across()
    {
        var menu = new Menu("long");
        menu.Add("Open " + new string('x', 400) + ".cs", () => { }, "Ctrl+O", "go-to-file");
        menu.Add("Copy", () => { });
        MenuPanel panel = Panel(menu);
        Assert.True(panel.Bounds.Width <= panel.MaxWidth(Skin) + 0.5f, $"{panel.Bounds.Width} is wider than {panel.MaxWidth(Skin)}");
        Assert.True(panel.Bounds.Width >= panel.MinWidth(Skin));
    }

    [Fact]
    public void A_destructive_item_is_painted_in_the_error_colour_and_the_others_are_not()
    {
        uint error = SpriteItem.PackColor(Skin.ErrorColor);

        var plain = new Menu("plain");
        plain.Add("Open", () => { }, icon: "go-to-file");
        plain.AddSeparator();
        plain.Add("Delete", () => { }, "Del", "trash");
        Assert.DoesNotContain(Paint(Panel(plain)), item => item.Color == error);

        var marked = new Menu("marked");
        marked.Add("Open", () => { }, icon: "go-to-file");
        marked.AddSeparator();
        marked.Add("Delete", () => { }, "Del", "trash", destructive: true);
        Assert.Contains(Paint(Panel(marked)), item => item.Color == error);
    }

    [Fact]
    public void A_destructive_item_that_cannot_be_picked_is_greyed_like_any_other()
    {
        uint error = SpriteItem.PackColor(Skin.ErrorColor);
        var menu = new Menu("greyed");
        menu.Add("Delete", () => { }, icon: "trash", destructive: true).IsEnabled = () => false;
        Assert.DoesNotContain(Paint(Panel(menu)), item => item.Color == error);
    }
}
