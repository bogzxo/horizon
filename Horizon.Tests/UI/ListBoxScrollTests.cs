using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>A list's rows glide to the one chosen, a fraction of a row a tick, and get all the way there.</summary>
public class ListBoxScrollTests
{
    [Fact]
    public void The_rows_glide_to_the_chosen_one_and_get_there()
    {
        var list = new ListBox { Rows = 10, Items = [.. Enumerable.Range(0, 40).Select(i => $"item {i}")] };
        list.Choose(30);
        Assert.Equal(21, list.Top);
        Assert.Equal(0.0f, list.Shown);

        list.UpdateTree(1.0f / 60.0f);
        Assert.InRange(list.Shown, 0.1f, 20.9f);

        for (int i = 0; i < 120; i++)
            list.UpdateTree(1.0f / 60.0f);
        Assert.Equal(21.0f, list.Shown);
    }
}
