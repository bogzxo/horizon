using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public class TreeViewportTests
{
    [Fact]
    public void Rebuilt_selection_can_be_restored_without_revealing_it()
    {
        var tree = new TreeView { Items = [new("a"), new("b")] };
        tree.Selected = tree.Items[0];
        tree.ScrollOffset = 240;
        tree.Items = [new("a"), new("b")];
        tree.SelectMany([tree.Items[0]]);
        tree.ScrollOffset = 240;
        Assert.Equal(240, tree.ScrollOffset);
        Assert.Same(tree.Items[0], tree.Selected);
        tree.ScrollOffset = float.NaN;
        Assert.Equal(0, tree.ScrollOffset);
    }
}
