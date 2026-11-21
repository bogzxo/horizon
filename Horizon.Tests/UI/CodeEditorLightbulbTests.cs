using System.Numerics;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

/// <summary>
/// The lightbulb in a code editor's gutter, on the line the program says has a fix: painted again when it comes and goes
/// (the codicon itself needs the GPU's atlas, the self test looks at it), and the line's row in the column of marks its own.
/// </summary>
public class CodeEditorLightbulbTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private static readonly UIRect Screen = new(Vector2.Zero, new Vector2(600.0f, 400.0f));

    private static void Paint(UIDrawList list, UIDrawList? previous, UIComponent root, int tick)
    {
        root.MeasureTree(Skin);
        root.ArrangeTree(Screen);
        list.Previous = previous;
        list.Begin(Skin, tick, 1.0f / 120.0f, tick / 120.0f);
        root.PaintTree(list);
        list.End();
    }

    [Fact]
    public void The_lightbulb_paints_again_as_it_comes_and_goes_and_its_row_is_its_own()
    {
        var panel = new ScrollPanel();
        var code = panel.Add(new CodeEditor(string.Join('\n', Enumerable.Range(0, 20).Select(i => $"int value{i} = totel;")))
        {
            Size = new Vector2(560.0f, 600.0f),
            Diagnostics = [new UICodeDiagnostic(2, 14, 5, "The name 'totel' does not exist (CS0103)")],
        });
        var a = new UIDrawList();
        var b = new UIDrawList();
        Paint(a, null, panel, 1);
        Paint(b, a, panel, 2);
        Paint(a, b, panel, 3);
        Assert.True(a.Reused > 0, "A settled editor is taken out of the list before.");

        int pressed = 0;
        code.ActionLine = 2;
        code.OnActionPressed = () => pressed++;
        Paint(b, a, panel, 4);
        Assert.Equal(0, b.Reused);
        Assert.True(code.ActionBounds is { } bulb && code.ActionAt(bulb.Center), "The lightbulb is where its press is taken.");

        // Its line's row in the column of marks is the lightbulb's, another line's isn't
        Vector2 onIt = new(code.Bounds.Min.X + 4.0f, 0.0f), onAnother = onIt;
        for (float y = code.Bounds.Min.Y; y < code.Bounds.Max.Y; y += 1.0f)
        {
            if (code.PositionAt(new Vector2(code.Bounds.Max.X - 10.0f, y)).Line == 2 && onIt.Y == 0.0f) onIt.Y = y + 2.0f;
            if (code.PositionAt(new Vector2(code.Bounds.Max.X - 10.0f, y)).Line == 5 && onAnother.Y == 0.0f) onAnother.Y = y + 2.0f;
        }
        Assert.True(code.ActionAt(onIt));
        Assert.False(code.ActionAt(onAnother));

        code.OnActionPressed = null;
        Assert.False(code.ActionAt(onIt));
        Paint(a, b, panel, 5);
        Assert.Equal(0, a.Reused);
        Assert.Null(code.ActionBounds);
        Assert.Equal(0, pressed);
    }
}
