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

    private static SpriteItem[] Paint(UISkin skin, MenuPanel[] panels, UIGlass? glass = null) => PaintList(skin, panels, glass).Items.ToArray();

    private static UIDrawList PaintList(UISkin skin, MenuPanel[] panels, UIGlass? glass = null)
    {
        var list = new UIDrawList();
        list.Begin(skin, dt: 1f / 120, clock: 1);
        foreach (var panel in panels) panel.Paint(list, OffMenu, glass);
        list.End();
        list.Begin(skin, dt: 1f / 120, clock: 2);
        foreach (var panel in panels) panel.Paint(list, OffMenu, glass);
        list.End();
        return list;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closing_and_replacement_menus_do_not_share_frames(bool glass)
    {
        // A frame between two ticks moves frames matched by their keys, so two menus coming in at once must not share
        // one, and neither may their quads (a shape's key is its clip, see SpriteItem.ShapeFlag). Out of a frame what
        // they paint is the newer tick's and can't be blended into the other's at all
        var skin = new UISkin(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));
        var closing = Panel(skin, new(-200, 200));
        var replacement = Panel(skin, new(100, 100));
        var list = PaintList(skin, [closing, replacement], glass ? UIGlass.Frosted : null);
        var frames = list.FrameList.Frames;
        Assert.Equal(frames.Count, frames.Select(frame => frame.Key).Distinct().Count());

        var quads = list.Items.ToArray().Where(item => (item.Flags & SpriteItem.ShapeFlag) == 0).ToArray();
        Assert.NotEmpty(quads);
        Assert.Equal(quads.Length, quads.Select(item => item.Key).Distinct().Count());
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
