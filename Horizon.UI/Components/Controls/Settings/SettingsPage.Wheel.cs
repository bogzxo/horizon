namespace Horizon.UI.Components;

// The wheel and the bars of a settings page. A wheel over a part of the page that can't scroll (the search, the sections
// when they all fit) scrolls the rows, and the bars grow with the page's zoom so there's something to take hold of.
public partial class SettingsPage
{
    // How thick the bars are at a zoom of 1
    private const float BAR = 6.0f;

    /// <summary>The scroll the rows are in, for a test.</summary>
    public ScrollPanel RowsScroll => scroll;

    /// <summary>The scroll the sections down the left are in, for a test.</summary>
    public ScrollPanel NavScroll => navScroll;

    // The bars as thick as the zoom has everything else
    private void FitBars()
    {
        scroll.BarWidth = BAR * zoom;
        navScroll.BarWidth = BAR * zoom;
    }

    protected internal override bool OnScroll(float delta) => scroll.Visible && scroll.OnScroll(delta);
}
