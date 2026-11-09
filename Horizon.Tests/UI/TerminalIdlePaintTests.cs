using System.Numerics;
using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.Tests.UI;

public sealed class TerminalIdlePaintTests
{
    private static readonly UISkin Skin = new(new UIFont(Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "cascadia_mono"), "cascadia_mono.ttf"));
    private static readonly UIRect Bounds = new(Vector2.Zero, new Vector2(600, 400));

    private static UIDrawList Paint(TerminalView view)
    {
        view.MeasureTree(Skin);
        view.ArrangeTree(Bounds);
        var list = new UIDrawList();
        list.Begin(Skin, 1, 0.05f, 0);
        view.PaintTree(list);
        list.End();
        return list;
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void Only_blink_transitions_change_an_idle_focused_terminal(int shape, bool blinks)
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(600, 400)));
        var view = compositor.CreateModule().AddComponent(new TerminalView { CursorGlide = 0 });
        view.Focus();
        view.Feed($"\u001b[{shape} q");
        Paint(view); // Settle the initial grid resize.
        var lit = Paint(view);
        view.UpdateTree(0.05f);
        Assert.True(lit.SameAs(Paint(view)));
        view.UpdateTree(0.50f);
        Assert.Equal(blinks, !lit.SameAs(Paint(view)));
        view.UpdateTree(0.53f);
        Assert.True(lit.SameAs(Paint(view)));

        view.Feed("output");
        Assert.False(lit.SameAs(Paint(view))); // Output still invalidates without a perpetual frame request.
    }

    [Fact]
    public void An_unfocused_terminal_does_not_change_when_its_blink_clock_advances()
    {
        var view = new TerminalView { CursorGlide = 0 };
        Paint(view);
        var outline = Paint(view);
        view.UpdateTree(0.6f);
        Assert.True(outline.SameAs(Paint(view)));
    }
}
