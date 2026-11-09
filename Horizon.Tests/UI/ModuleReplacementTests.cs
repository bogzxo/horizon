using System.Numerics;
using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public sealed class ModuleReplacementTests
{
    [Fact]
    public void Replacement_preserves_order_and_previous_reader_snapshot()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var first = compositor.CreateModule();
        var current = compositor.CreateModule();
        var last = compositor.CreateModule();
        var before = compositor.Modules;
        var replacement = new UIModule(compositor);

        Assert.True(compositor.ReplaceModule(current, replacement));
        Assert.Equal(new[] { first, replacement, last }, compositor.Modules);
        Assert.Equal(new[] { first, current, last }, before);
        Assert.False(current.Enabled);
        Assert.True(replacement.Enabled);
        Assert.False(compositor.ReplaceModule(current, new UIModule(compositor)));
        Assert.Equal(new[] { first, replacement, last }, compositor.Modules);
    }

    [Fact]
    public void Replacement_clears_only_focus_owned_by_the_retired_module()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var first = compositor.CreateModule();
        var current = compositor.CreateModule();
        var otherFocus = first.AddComponent(new Button("Other"));
        compositor.Focus = otherFocus;
        var replacement = new UIModule(compositor);
        Assert.True(compositor.ReplaceModule(current, replacement));
        Assert.Same(otherFocus, compositor.Focus);
        var nextFocus = replacement.AddComponent(new Button("Next"));
        compositor.Focus = nextFocus;
        Assert.True(compositor.ReplaceModule(replacement, new UIModule(compositor)));
        Assert.Null(compositor.Focus);
    }

    [Fact]
    public void Replacement_rejects_duplicate_and_foreign_modules_without_mutation()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        using var other = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var first = compositor.CreateModule();
        var second = compositor.CreateModule();
        var before = compositor.Modules;
        Assert.Throws<ArgumentException>(() => compositor.ReplaceModule(first, second));
        Assert.Throws<ArgumentException>(() => compositor.ReplaceModule(first, new UIModule(other)));
        Assert.Same(before, compositor.Modules);
        Assert.True(compositor.ReplaceModule(first, first));
        Assert.Same(before, compositor.Modules);
    }
}
