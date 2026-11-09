using System.Numerics;
using System.Reflection;
using Horizon.Rendering;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

public class TablePaintTests
{
    private sealed class PaintedTable : Table
    {
        public void Draw(UIDrawList list) => Paint(list);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hover_and_selection_keep_quad_identity_and_geometry(bool frozen)
    {
        var skin = new UISkin(new UIFont(Path.GetTempPath(), "no-font-here.ttf"));
        var table = new PaintedTable
        {
            Columns = [new("Load", 80), new("Value", 80)],
            Rows = [[new("F"), new("1000")], [new("L"), new("2")]],
            RowHeight = 24, FreezeFirst = frozen
        };
        table.Refresh();
        table.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(160, 140)));
        SpriteItem[] Paint()
        {
            var list = new UIDrawList();
            list.Begin(skin);
            table.Draw(list);
            list.End();
            return list.Items.ToArray();
        }
        var baseline = Paint();
        foreach (var hover in new[] { (0, 0), (1, 1), (-1, 0), (-1, -1) })
        {
            typeof(Table).GetField("hovered", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(table, hover);
            var current = Paint();
            Assert.Equal(baseline.Length, current.Length);
            Assert.Equal(baseline.Select(item => (item.Key, item.Origin, item.AxisX, item.AxisY, item.Flags)),
                current.Select(item => (item.Key, item.Origin, item.AxisX, item.AxisY, item.Flags)));
        }
        table.Selected = (0, 1);
        table.Rows[1][0].Fill = new Vector4(1, 0, 0, 0.2f);
        Assert.Equal(baseline.Select(item => (item.Key, item.Origin, item.AxisX, item.AxisY)),
            Paint().Select(item => (item.Key, item.Origin, item.AxisX, item.AxisY)));
    }
}
