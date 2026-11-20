using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// A component's children put in, taken out and moved about, one at a time and several at once, coming out in the order
/// they should and knowing who they're inside of.
/// </summary>
public class UIChildrenTests
{
    private static (Panel Parent, Panel[] Children) Five()
    {
        var parent = new Panel();
        var children = Enumerable.Range(0, 5).Select(_ => new Panel()).ToArray();
        foreach (Panel child in children)
            parent.Add(child);
        return (parent, children);
    }

    private static int[] Order(Panel parent, Panel[] children) => [.. parent.Children.Select(child => Array.IndexOf(children, child))];

    [Theory]
    [InlineData(1, 3, new[] { 0, 2, 3, 1, 4 })]
    [InlineData(3, 1, new[] { 0, 3, 1, 2, 4 })]
    [InlineData(0, 99, new[] { 1, 2, 3, 4, 0 })]
    [InlineData(4, -5, new[] { 4, 0, 1, 2, 3 })]
    [InlineData(2, 2, new[] { 0, 1, 2, 3, 4 })]
    public void A_child_moved_goes_where_it_was_asked_and_the_rest_keep_their_order(int from, int to, int[] wanted)
    {
        var (parent, children) = Five();
        Assert.True(parent.MoveChild(children[from], to));
        Assert.Equal(wanted, Order(parent, children));
    }

    [Theory]
    [InlineData(0, new[] { 1, 2, 3, 4 })]
    [InlineData(2, new[] { 0, 1, 3, 4 })]
    [InlineData(4, new[] { 0, 1, 2, 3 })]
    public void A_child_taken_out_leaves_the_rest_in_order(int which, int[] wanted)
    {
        var (parent, children) = Five();
        Assert.True(parent.Remove(children[which]));
        Assert.Null(children[which].Parent);
        Assert.Equal(wanted, Order(parent, children));
        Assert.False(parent.Remove(children[which]));
    }

    [Fact]
    public void Several_put_in_at_once_go_at_the_end_in_their_order_out_of_where_they_were()
    {
        var (parent, children) = Five();
        var elsewhere = new Panel();
        var moved = new Panel();
        elsewhere.Add(moved);
        var fresh = new Panel();

        parent.AddRange([moved, fresh]);

        Assert.Equal([children[0], children[1], children[2], children[3], children[4], moved, fresh], parent.Children);
        Assert.Same(parent, moved.Parent);
        Assert.Same(parent, fresh.Parent);
        Assert.Empty(elsewhere.Children);
    }

    [Fact]
    public void Several_taken_out_at_once_leave_the_rest_in_order_and_pass_over_strangers()
    {
        var (parent, children) = Five();
        var stranger = new Panel();
        new Panel().Add(stranger);

        parent.RemoveRange([children[3], children[0], stranger]);

        Assert.Equal([1, 2, 4], Order(parent, children));
        Assert.Null(children[0].Parent);
        Assert.Null(children[3].Parent);
        Assert.NotNull(stranger.Parent);
    }

    [Fact]
    public void A_component_cannot_be_put_inside_itself_several_at_a_time_either()
    {
        var (parent, children) = Five();
        Assert.Throws<InvalidOperationException>(() => children[0].AddRange([parent]));
    }
}
