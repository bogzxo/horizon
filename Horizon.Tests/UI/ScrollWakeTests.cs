using System.Numerics;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

public class ScrollWakeTests
{
    [Fact]
    public void Horizontal_table_wheel_eases_accumulates_and_clamps_after_resize()
    {
        var table = new Table { Columns = [new UITableColumn("one", 400), new UITableColumn("two", 400)] };
        table.MeasureTree(Skin);
        table.ArrangeTree(UIRect.FromCenter(Vector2.Zero, new Vector2(300, 200)));
        Assert.True(table.ScrollWheel(-1, Skin, across: true));
        Assert.Equal(0, table.HorizontalScroll);
        table.UpdateTree(0.25f);
        float first = table.HorizontalScroll;
        Assert.True(first > 0);
        for (int i = 0; i < 120; i++) table.UpdateTree(1f / 60);
        float step = table.HorizontalScroll;
        Assert.True(first < step * 0.4f);
        table.ScrollWheel(-1, Skin, across: true);
        table.ScrollWheel(-1, Skin, across: true);
        for (int i = 0; i < 120; i++) table.UpdateTree(1f / 60);
        Assert.Equal(step * 3, table.HorizontalScroll, 2);
        table.ArrangeTree(UIRect.FromCenter(Vector2.Zero, new Vector2(1000, 200)));
        table.UpdateTree(1f / 60);
        Assert.Equal(0, table.HorizontalScroll);
        Assert.False(table.ScrollWheel(-1, Skin, across: true));
    }

    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    [Fact]
    public void Code_editor_wake_scroll_matches_a_normal_first_frame()
    {
        float Move(float dt)
        {
            var editor = new CodeEditor(string.Join('\n', Enumerable.Repeat("some code", 200)));
            void Paint(int frame)
            {
                editor.MeasureTree(Skin);
                editor.ArrangeTree(UIRect.FromCenter(Vector2.Zero, new Vector2(500, 200)));
                var draw = new UIDrawList();
                draw.Begin(Skin, frame, 1f / 60, frame / 60f);
                editor.PaintTree(draw); draw.End();
            }
            Paint(1);
            editor.UpdateTree(1f / 60);
            Paint(2);
            float before = editor.ScrollPixels;
            Assert.True(editor.OnScroll(-1));
            editor.UpdateTree(dt);
            Paint(2);
            return editor.ScrollPixels - before;
        }
        float normal = Move(1f / 60);
        Assert.True(MathF.Abs(normal) > 0.01f);
        Assert.Equal(normal, Move(0.25f), 2);
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.25f)]
    [InlineData(1f)]
    public void First_wheel_step_does_not_include_time_spent_idle(float idle)
    {
        var panel = new ScrollPanel();
        panel.Add(new Panel { Size = new Vector2(240, 1000) });
        panel.MeasureTree(Skin);
        panel.ArrangeTree(UIRect.FromCenter(Vector2.Zero, new Vector2(260, 120)));
        Assert.True(panel.OnScroll(-1));
        panel.UpdateTree(idle);
        Assert.InRange(panel.Offset, 0.1f, panel.WheelStep * 0.4f);
        for (int i = 0; i < 120; i++) panel.UpdateTree(1f / 60);
        Assert.Equal(panel.WheelStep, panel.Offset, 2);
    }

    [Fact]
    public void List_starts_gently_after_idle_and_repeated_wheel_input_still_accumulates()
    {
        var list = new ListBox { Rows = 10, Items = [.. Enumerable.Range(0, 100).Select(i => i.ToString())] };
        Assert.True(list.OnScroll(-1));
        list.UpdateTree(0.25f);
        Assert.InRange(list.Shown, 0.01f, list.Top * 0.4f);
        int first = list.Top;
        Assert.True(list.OnScroll(-1));
        list.UpdateTree(1f / 30);
        Assert.Equal(first * 2, list.Top);
        for (int i = 0; i < 120; i++) list.UpdateTree(1f / 60);
        Assert.Equal(list.Top, list.Shown);
    }
}
