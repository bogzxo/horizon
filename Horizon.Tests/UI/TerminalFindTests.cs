using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public class TerminalFindTests
{
    private static TerminalView View()
    {
        var view = new TerminalView();
        view.Screen.Resize(40, 4);
        for (int i = 0; i < 40; i++)
            view.Screen.Feed($"match row {i}\r\n");
        return view;
    }

    [Fact]
    public void Inline_search_yields_between_batches_and_matches_the_synchronous_API()
    {
        using var view = View();
        view.StartFind("match");
        view.AdvanceFind(2, 1000);
        Assert.True(view.FindRunning);
        Assert.Equal(2, view.Found.Count);
        Assert.False(view.FindNext(1));
        while (view.FindRunning) view.AdvanceFind(2, 1000);
        int found = view.Found.Count;
        Assert.Equal(40, found);
        Assert.Equal(found, view.Find("match"));
        Assert.True(view.FindNext(1));
    }

    [Fact]
    public void A_new_query_or_close_cancels_the_old_batch()
    {
        using var view = View();
        view.StartFind("match");
        view.AdvanceFind(2, 1000);
        view.StartFind("absent");
        Assert.Equal(0, view.Found.Count);
        while (view.FindRunning) view.AdvanceFind(2, 1000);
        Assert.Equal(0, view.Found.Count);
        view.StartFind("match");
        view.ClearFind();
        view.AdvanceFind(10, 1000);
        Assert.False(view.FindRunning);
        Assert.Equal(0, view.Found.Count);
    }

    [Fact]
    public void Inline_results_are_bounded_without_limiting_explicit_API_searches()
    {
        using var view = View();
        view.StartFind("match");
        view.AdvanceFind(256, 3);
        Assert.False(view.FindRunning);
        Assert.True(view.FindLimited);
        Assert.Equal(3, view.Found.Count);
        Assert.Equal(40, view.Find("match"));
        Assert.False(view.FindLimited);
    }

    [Fact]
    public void Scrolling_output_restarts_a_batch_with_current_row_coordinates()
    {
        using var view = View();
        view.StartFind("match");
        view.AdvanceFind(2, 1000);
        view.Screen.Feed("another match\r\n");
        while (view.FindRunning) view.AdvanceFind(2, 1000);
        Assert.Equal(41, view.Found.Count);
        Assert.Equal(view.Found.Count, view.Find("match"));
    }
}
