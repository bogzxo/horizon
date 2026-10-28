using System.Numerics;
using Horizon.Rendering.Spriting;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

public class MenuPaintTests
{
    private static readonly Vector2 OffMenu = new(float.NegativeInfinity);

    private static MenuPanel Panel(UISkin skin, Vector2 at)
    {
        var menu = new Menu("file");
        menu.Add("Open", () => { });
        menu.AddSeparator();
        menu.Add("Rename", () => { });
        menu.AddSeparator();
        menu.Add("Delete", () => { });
        return new MenuPanel(menu, skin, 0, MenuPlacement.AtPoint, new(at, at), new(new(-500), new(500)));
    }

    private static SpriteItem[] Paint(UISkin skin, MenuPanel[] panels, UIGlass? glass = null)
    {
        var list = new UIDrawList();
        list.Begin(skin, dt: 1f / 120, clock: 1);
        foreach (var panel in panels) panel.Paint(list, OffMenu, glass);
        list.End();
        list.Begin(skin, dt: 1f / 120, clock: 2);
        foreach (var panel in panels) panel.Paint(list, OffMenu, glass);
        list.End();
        return list.Items.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closing_and_replacement_menus_do_not_share_animation_keys(bool glass)
    {
        var skin = new UISkin(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));
        var closing = Panel(skin, new(-200, 200));
        var replacement = Panel(skin, new(100, 100));
        var items = Paint(skin, [closing, replacement], glass ? UIGlass.Frosted : null);
        Assert.NotEmpty(items);
        Assert.Equal(items.Length, items.Select(item => item.Key).Distinct().Count());
    }

    [Fact]
    public void Existing_menu_keeps_its_keys_when_another_menu_is_painted_before_it()
    {
        var skin = new UISkin(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));
        var existing = Panel(skin, new(100, 100));
        var alone = Paint(skin, [existing]);
        var preceding = Panel(skin, new(-200, 200));
        var together = Paint(skin, [preceding, existing]);
        var after = together[^alone.Length..];
        Assert.Equal(alone.Select(item => (item.Key, item.Origin, item.AxisX, item.AxisY)),
            after.Select(item => (item.Key, item.Origin, item.AxisX, item.AxisY)));
    }
}
