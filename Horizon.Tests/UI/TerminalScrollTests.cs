using Horizon.UI.Components;
using Horizon.UI.Drawing;

namespace Horizon.Tests.UI;

/// <summary>What the scroll bar and the minimap of a terminal go by, the rows gone off the top and how far a map scrolls.</summary>
public class TerminalScrollTests
{
    [Fact]
    public void Every_row_gone_off_the_top_is_counted_whatever_the_scrollback_lets_go_of()
    {
        var screen = new TerminalScreen(20, 5) { MaxScrollback = 10 };
        for (int i = 0; i < 30; i++)
            screen.Feed($"row {i}\r\n");

        Assert.Equal(10, screen.Scrollback.Count);
        Assert.Equal(26, screen.RowsScrolledOff);
    }

    [Fact]
    public void A_program_with_the_whole_screen_scrolls_nothing_off()
    {
        var screen = new TerminalScreen(20, 5);
        screen.Feed("\u001b[?1049h");
        for (int i = 0; i < 30; i++)
            screen.Feed($"row {i}\r\n");

        Assert.Equal(0, screen.RowsScrolledOff);
        Assert.Empty(screen.Scrollback);
    }

    [Fact]
    public void A_map_scrolls_whole_rows_from_none_at_the_top_to_all_it_has_to_at_the_bottom()
    {
        // A hundred rows of two in a map forty tall, sixty of them out of sight
        Assert.Equal(0, UIMinimap.ScrolledRows(100, 2.0f, 80.0f, 0.0f));
        Assert.Equal(30, UIMinimap.ScrolledRows(100, 2.0f, 80.0f, 0.5f));
        Assert.Equal(60, UIMinimap.ScrolledRows(100, 2.0f, 80.0f, 1.0f));

        // All of them in sight, nothing to scroll
        Assert.Equal(0, UIMinimap.ScrolledRows(30, 2.0f, 80.0f, 1.0f));
    }

    [Fact]
    public void A_row_of_a_map_is_a_whole_number_of_pixels()
    {
        Assert.Equal(2.0f, UIMinimap.Row(1.0f));
        Assert.Equal(3.0f, UIMinimap.Row(1.5f) * 1.5f, 3);
        Assert.Equal(2.0f, UIMinimap.Row(1.11f) * 1.11f, 3);
        Assert.Equal(0.0f, UIMinimap.Gap(UIMinimap.Row(1.0f), 1.0f));
        Assert.True(UIMinimap.Gap(UIMinimap.Row(1.5f), 1.5f) > 0.0f);
    }
}
