using System.Numerics;
using Horizon.Rendering.Spriting;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public class CodeEditorRemoteCaretsTests
{
    [Fact]
    public void Wrapped_selection_carets_and_pointer_paint_and_clear_independently()
    {
        var font = new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf");
        var skin = new UISkin(font);
        var panel = new ScrollPanel();
        var editor = panel.Add(new CodeEditor(new string('w', 72) + "\n\nlast" + string.Concat(Enumerable.Repeat("\nnext", 100))) { Size = new(560, 700), WordWrap = true, Minimap = false });
        Vector4 peach = new(0.99f, 0.43f, 0.19f, 1), mint = new(0.13f, 0.88f, 0.67f, 1);
        var alice = new UICodeRemoteCaret("a", "Alice", 1, 0, 0, 2, peach, 2, 2);
        var bob = new UICodeRemoteCaret("b", "Bob", 2, 4, 2, 4, mint);
        editor.RemoteCarets = [alice, bob];
        var caretBefore = editor.Document.Caret;
        var previous = Paint(null, 1);
        Assert.True(editor.RowCount > editor.Document.LineCount);
        Assert.True(In(previous, peach with { W = 0.20f }).Length > 1);
        Assert.True(In(previous, mint).Length > 0, $"Bob at {editor.PlaceOf(new(2, 4))}, editor {editor.Bounds}, rows {editor.RowCount}");
        var withMouse = In(previous, peach);
        editor.RemoteCarets = [alice with { PointerLine = null, PointerColumn = null }, bob];
        var withoutMouse = Paint(previous, 2);
        Assert.True(In(withoutMouse, peach).Length < withMouse.Length);
        Assert.NotEmpty(In(withoutMouse, peach));
        Assert.Equal(In(previous, mint).Length, In(withoutMouse, mint).Length);
        editor.RemoteCarets = [bob];
        var onlyBob = Paint(withoutMouse, 3);
        Assert.Empty(In(onlyBob, peach));
        Assert.Empty(In(onlyBob, peach with { W = 0.20f }));
        Assert.NotEmpty(In(onlyBob, mint));
        editor.GoTo(99);
        panel.UpdateTree(0.1f);
        var scrolled = Paint(onlyBob, 4);
        Assert.Empty(In(scrolled, mint));
        editor.GoTo(caretBefore.Line, caretBefore.Column);
        panel.UpdateTree(0.1f);
        var returned = Paint(scrolled, 5);
        Assert.NotEmpty(In(returned, mint));
        editor.RemoteCarets = [];
        Assert.Empty(In(Paint(returned, 6), mint));
        Assert.Equal(caretBefore, editor.Document.Caret);
        Assert.False(editor.Document.HasSelection);

        UIDrawList Paint(UIDrawList? before, int tick)
        {
            panel.MeasureTree(skin);
            panel.ArrangeTree(new UIRect(Vector2.Zero, new Vector2(560, 720)));
            var list = new UIDrawList { Previous = before };
            list.Begin(skin, tick, 0.01f, tick * 0.01f);
            panel.PaintTree(list);
            list.End();
            return list;
        }

        static SpriteItem[] In(UIDrawList list, Vector4 colour) =>
            list.Items.ToArray().Where(item => item.Color == SpriteItem.PackColor(colour)).ToArray();
    }
}
