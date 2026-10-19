using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// The tree of splits a terminal tab is divided by, with strings in it and no window anywhere near.
/// </summary>
public class SplitTreeTests
{
    private static readonly UIRect Whole = new(new Vector2(0, 0), new Vector2(800, 600));

    private static string Shape(SplitTree<string> tree) => tree.Describe(name => name);

    [Fact]
    public void Splitting_puts_the_newcomer_beside_or_under_the_leaf()
    {
        var tree = new SplitTree<string>();
        var a = tree.Add("a");
        var b = tree.Split(a, SplitOrientation.Horizontal, "b");
        Assert.Equal("[a|b]", Shape(tree));

        tree.Split(b, SplitOrientation.Vertical, "c");
        Assert.Equal("[a|[b/c]]", Shape(tree));

        tree.Split(a, SplitOrientation.Horizontal, "z", before: true);
        Assert.Equal("[[z|a]|[b/c]]", Shape(tree));
        Assert.Equal(4, tree.Count);
        Assert.Equal(["z", "a", "b", "c"], tree.Leaves.Select(leaf => leaf.Content));
    }

    [Fact]
    public void Removing_a_leaf_gives_its_sibling_the_whole_split()
    {
        var tree = new SplitTree<string>();
        var a = tree.Add("a");
        var b = tree.Split(a, SplitOrientation.Horizontal, "b");
        var c = tree.Split(b, SplitOrientation.Vertical, "c");

        Assert.True(tree.Remove(b));
        Assert.Equal("[a|c]", Shape(tree));
        Assert.Null(c.Parent!.Parent);

        Assert.True(tree.Remove(a));
        Assert.Equal("c", Shape(tree));
        Assert.Same(c, tree.Root);

        Assert.True(tree.Remove(c));
        Assert.True(tree.IsEmpty);
        Assert.False(tree.Remove(c));
    }

    [Fact]
    public void Layout_shares_the_room_by_the_ratio_with_a_gap_between()
    {
        var tree = new SplitTree<string>();
        var a = tree.Add("a");
        var b = tree.Split(a, SplitOrientation.Horizontal, "b");
        tree.Layout(Whole, gap: 4.0f);

        Assert.Equal(new UIRect(new Vector2(0, 0), new Vector2(398, 600)), a.Bounds);
        Assert.Equal(new UIRect(new Vector2(402, 0), new Vector2(800, 600)), b.Bounds);

        // Dragged, the left one takes a quarter
        a.Parent!.Ratio = 0.25f;
        tree.Layout(Whole, gap: 4.0f);
        Assert.Equal(199, a.Bounds.Width);
        Assert.Equal(597, b.Bounds.Width);

        // One over the other, the first half is the top one, y going up
        var c = tree.Split(b, SplitOrientation.Vertical, "c");
        tree.Layout(Whole, gap: 4.0f);
        Assert.Equal(600, b.Bounds.Max.Y);
        Assert.Equal(298, b.Bounds.Height);
        Assert.Equal(0, c.Bounds.Min.Y);
        Assert.Equal(298, c.Bounds.Height);
    }

    [Fact]
    public void A_pane_is_never_squeezed_under_the_least_unless_it_is_animating()
    {
        var tree = new SplitTree<string>();
        var a = tree.Add("a");
        var b = tree.Split(a, SplitOrientation.Horizontal, "b", share: 0.0f);
        tree.Layout(Whole, gap: 4.0f, least: 40.0f);
        Assert.Equal(40, b.Bounds.Width);

        b.Parent!.Animating = true;
        tree.Layout(Whole, gap: 4.0f, least: 40.0f);
        Assert.Equal(0, b.Bounds.Width);
    }

    [Fact]
    public void Neighbours_are_found_by_where_the_panes_were_laid_out()
    {
        var tree = new SplitTree<string>();
        var a = tree.Add("a");
        var b = tree.Split(a, SplitOrientation.Horizontal, "b");
        var c = tree.Split(b, SplitOrientation.Vertical, "c");
        tree.Layout(Whole);

        Assert.Same(b, tree.Neighbour(a, SplitDirection.Right));
        Assert.Same(a, tree.Neighbour(b, SplitDirection.Left));
        Assert.Same(a, tree.Neighbour(c, SplitDirection.Left));
        Assert.Same(c, tree.Neighbour(b, SplitDirection.Down));
        Assert.Same(b, tree.Neighbour(c, SplitDirection.Up));
        Assert.Null(tree.Neighbour(a, SplitDirection.Left));
        Assert.Null(tree.Neighbour(a, SplitDirection.Up));
        Assert.Null(tree.Neighbour(b, SplitDirection.Right));
    }

    [Fact]
    public void An_edge_belongs_to_the_nearest_split_it_is_an_inner_edge_of()
    {
        var tree = new SplitTree<string>();
        var a = tree.Add("a");
        var b = tree.Split(a, SplitOrientation.Horizontal, "b");
        var c = tree.Split(b, SplitOrientation.Vertical, "c");
        var outer = a.Parent!;
        var inner = c.Parent!;

        Assert.Equal((outer, true), tree.EdgeOwner(a, SplitDirection.Right));
        Assert.Null(tree.EdgeOwner(a, SplitDirection.Left));
        Assert.Null(tree.EdgeOwner(a, SplitDirection.Down));

        // c's left edge is the outer split's, which it is in the second half of, its top edge the inner split's
        Assert.Equal((outer, false), tree.EdgeOwner(c, SplitDirection.Left));
        Assert.Equal((inner, false), tree.EdgeOwner(c, SplitDirection.Up));
        Assert.Equal((inner, true), tree.EdgeOwner(b, SplitDirection.Down));
        Assert.Null(tree.EdgeOwner(b, SplitDirection.Up));
    }

    [Fact]
    public void Find_knows_what_holds_a_thing()
    {
        var tree = new SplitTree<string>();
        var a = tree.Add("a");
        var b = tree.Split(a, SplitOrientation.Vertical, "b");

        Assert.Same(b, tree.Find("b"));
        Assert.Null(tree.Find("nothing"));
        Assert.Throws<ArgumentException>(() => tree.Split(a.Parent!, SplitOrientation.Horizontal, "x"));
        Assert.Throws<InvalidOperationException>(() => tree.Add("y"));
    }
}
