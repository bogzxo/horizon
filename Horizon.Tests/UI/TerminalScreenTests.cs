using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// The terminal's screen, fed the bytes a program writes and read back as a grid, no window anywhere near it.
/// </summary>
public class TerminalScreenTests
{
    private static TerminalScreen Screen(int columns = 20, int rows = 5) => new(columns, rows);

    [Fact]
    public void Text_goes_where_the_cursor_is_and_wraps_at_the_edge()
    {
        var screen = Screen(10, 3);
        screen.Feed("hello\r\nworld, this wraps");

        Assert.Equal("hello\nworld, thi\ns wraps", screen.Text());
        Assert.True(screen.Grid[1].Wrapped);
        Assert.False(screen.Grid[0].Wrapped);
        Assert.Equal((7, 2), (screen.CursorX, screen.CursorY));
    }

    [Fact]
    public void A_line_feed_at_the_bottom_scrolls_the_top_row_into_the_scrollback()
    {
        var screen = Screen(10, 2);
        screen.Feed("one\r\ntwo\r\nthree");

        Assert.Equal("two\nthree", screen.Text());
        Assert.Single(screen.Scrollback);
        Assert.Equal("one", screen.Scrollback[0].Text());
        Assert.Equal(3, screen.TotalRows);
    }

    [Fact]
    public void The_cursor_is_moved_by_the_control_sequences()
    {
        var screen = Screen();
        screen.Feed("\u001b[3;5Hx\u001b[2Ay\u001b[3Dz\u001b[Gw");

        Assert.Equal('x', screen[4, 2].CodePoint);
        Assert.Equal('y', screen[5, 0].CodePoint);
        Assert.Equal('z', screen[3, 0].CodePoint);
        Assert.Equal('w', screen[0, 0].CodePoint);
    }

    [Fact]
    public void Erasing_clears_what_it_says_and_keeps_the_background()
    {
        var screen = Screen(10, 3);
        screen.Feed("abcdefghij\r\nklmnopqrst\r\nuvwxyz");
        screen.Feed("\u001b[2;4H\u001b[44m\u001b[K");

        Assert.Equal("abcdefghij\nklm\nuvwxyz", screen.Text(), ignoreLineEndingDifferences: true);
        Assert.Equal(TerminalColor.Indexed(4), screen[5, 1].Style.Background);

        screen.Feed("\u001b[J");
        Assert.Equal("abcdefghij\nklm\n", screen.Text());
        screen.Feed("\u001b[2J");
        Assert.Equal("\n\n", screen.Text());
    }

    [Fact]
    public void Graphics_set_the_colours_and_attributes_with_the_colon_forms_too()
    {
        var screen = Screen();
        screen.Feed("\u001b[1;3;4;31;48;5;200mA\u001b[0;38;2;10;20;30;4:3mB\u001b[mC");

        TerminalStyle a = screen[0, 0].Style, b = screen[1, 0].Style, c = screen[2, 0].Style;
        Assert.True(a.Has(TerminalAttributes.Bold) && a.Has(TerminalAttributes.Italic) && a.Has(TerminalAttributes.Underline));
        Assert.Equal(TerminalColor.Indexed(1), a.Foreground);
        Assert.Equal(TerminalColor.Indexed(200), a.Background);
        Assert.Equal(TerminalColor.Rgb(10, 20, 30), b.Foreground);
        Assert.Equal(TerminalUnderline.Curly, b.Underline);
        Assert.False(b.Has(TerminalAttributes.Bold));
        Assert.Equal(TerminalStyle.Plain, c);
    }

    [Fact]
    public void A_wide_character_takes_two_cells_and_a_mark_combines_onto_the_one_before()
    {
        var screen = Screen(6, 2);
        screen.Feed("a中b́c中");

        Assert.True(screen[1, 0].Style.Has(TerminalAttributes.Wide));
        Assert.True(screen[2, 0].IsContinuation);
        Assert.Equal('b', screen[3, 0].CodePoint);
        Assert.Equal("́", screen[3, 0].Combining);
        Assert.Equal('c', screen[4, 0].CodePoint);

        // The last one didn't fit in the one cell left, it went to the next line
        Assert.Equal(0x4e2d, screen[0, 1].CodePoint);
        Assert.Equal("a中b́c\n中", screen.Text());
    }

    [Fact]
    public void The_scrolling_region_scrolls_on_its_own_and_insert_and_delete_lines_stay_inside_it()
    {
        var screen = Screen(5, 4);
        screen.Feed("1\r\n2\r\n3\r\n4");
        screen.Feed("\u001b[2;3r\u001b[3;1H\n");

        Assert.Equal("1\n3\n\n4", screen.Text());
        Assert.Empty(screen.Scrollback);

        screen.Feed("\u001b[2;1H\u001b[L");
        Assert.Equal("1\n\n3\n4", screen.Text());
    }

    [Fact]
    public void The_alternate_screen_is_its_own_and_the_main_one_comes_back_as_it_was()
    {
        var screen = Screen(10, 2);
        screen.Feed("main\u001b[?1049h\u001b[Hfull screen\u001b[?1049l");

        Assert.False(screen.AlternateScreen);
        Assert.Equal("main\n", screen.Text());
        Assert.Equal((4, 0), (screen.CursorX, screen.CursorY));

        screen.Feed("\u001b[?1049h");
        Assert.True(screen.AlternateScreen);
        Assert.Equal("\n", screen.Text());
    }

    [Fact]
    public void Modes_are_remembered_and_the_program_is_answered()
    {
        var screen = Screen();
        screen.Feed("\u001b[?1h\u001b[?2004h\u001b[?1002h\u001b[?1006h\u001b[?25l\u001b[>1u");

        Assert.True(screen.ApplicationCursorKeys);
        Assert.True(screen.BracketedPaste);
        Assert.Equal(TerminalMouseMode.Drags, screen.MouseMode);
        Assert.True(screen.MouseSgr);
        Assert.False(screen.CursorVisible);
        Assert.Equal(1, screen.KeyboardFlags);

        screen.Feed("\u001b[<u\u001b[3;4H\u001b[6n\u001b[c\u001b[?u");
        Assert.Equal(0, screen.KeyboardFlags);
        Assert.Equal("\u001b[3;4R\u001b[?62;22c\u001b[?0u", screen.Responses.ToString());
    }

    [Fact]
    public void The_title_and_a_hyperlink_come_through_osc()
    {
        var screen = Screen();
        screen.Feed("\u001b]0;my shell\u0007\u001b]8;;https://example.com\u001b\\link\u001b]8;;\u0007 plain");

        Assert.Equal("my shell", screen.Title);
        Assert.Equal("https://example.com", screen.Links[screen[0, 0].Style.Link]);
        Assert.Equal(0, screen[5, 0].Style.Link);
    }

    [Fact]
    public void Line_drawing_and_tabs_and_a_bell()
    {
        var screen = Screen(20, 2);
        int rang = 0;
        screen.Bell = () => rang++;
        screen.Feed("\u001b(0lqk\u001b(B\ta\u0007");

        Assert.Equal("┌─┐", screen.Grid[0].Text(0, 3));
        Assert.Equal('a', screen[8, 0].CodePoint);
        Assert.Equal(1, rang);
    }

    [Fact]
    public void Resizing_reflows_the_lines_that_wrapped_and_keeps_the_cursor_on_its_line()
    {
        var screen = Screen(10, 3);
        screen.Feed("0123456789abcdef\r\nshort\r\nprompt> ");
        Assert.Equal("0123456789\nabcdef\nshort", string.Join('\n', Enumerable.Range(0, 3).Select(i => screen.RowAt(i).Text())));

        screen.Resize(20, 3);
        Assert.Equal("0123456789abcdef\nshort\nprompt>", screen.Text());
        Assert.Equal((8, 2), (screen.CursorX, screen.CursorY));
        Assert.Empty(screen.Scrollback);

        screen.Resize(6, 3);
        Assert.Equal("short\nprompt\n>", string.Join('\n', screen.Grid.Select(row => row.Text())));
        Assert.Equal((2, 2), (screen.CursorX, screen.CursorY));
        Assert.Equal("012345\n6789ab\ncdef", string.Join('\n', screen.Scrollback.Select(row => row.Text())));
    }

    [Fact]
    public void Half_a_sequence_waits_for_the_rest_and_utf8_is_put_together_across_feeds()
    {
        var screen = Screen();
        screen.Feed([0x1B, (byte)'[', (byte)'3']);
        screen.Feed([(byte)'1', (byte)'m', 0xE2, 0x82]);
        screen.Feed([0xAC]);

        Assert.Equal(0x20AC, screen[0, 0].CodePoint);
        Assert.Equal(TerminalColor.Indexed(1), screen[0, 0].Style.Foreground);
    }

    [Fact]
    public void The_keys_come_out_as_a_terminal_sends_them()
    {
        Assert.Equal("\u001b[A", TerminalKeys.Key(Silk.NET.Input.Key.Up, TerminalKeys.Modifiers.None, false, 0));
        Assert.Equal("\u001bOA", TerminalKeys.Key(Silk.NET.Input.Key.Up, TerminalKeys.Modifiers.None, true, 0));
        Assert.Equal("\u001b[1;5C", TerminalKeys.Key(Silk.NET.Input.Key.Right, TerminalKeys.Modifiers.Control, false, 0));
        Assert.Equal("\u0003", TerminalKeys.Key(Silk.NET.Input.Key.C, TerminalKeys.Modifiers.Control, false, 0));
        Assert.Equal("\u001b\u0003", TerminalKeys.Key(Silk.NET.Input.Key.C, TerminalKeys.Modifiers.Control | TerminalKeys.Modifiers.Alt, false, 0));
        Assert.Equal("\u001b[15~", TerminalKeys.Key(Silk.NET.Input.Key.F5, TerminalKeys.Modifiers.None, false, 0));
        Assert.Equal("\u001b[Z", TerminalKeys.Key(Silk.NET.Input.Key.Tab, TerminalKeys.Modifiers.Shift, false, 0));
        Assert.Null(TerminalKeys.Key(Silk.NET.Input.Key.A, TerminalKeys.Modifiers.None, false, 0));

        // The kitty protocol tells escape and control with a letter apart
        Assert.Equal("\u001b[27;5u", TerminalKeys.Key(Silk.NET.Input.Key.Escape, TerminalKeys.Modifiers.Control, false, 1));
        Assert.Equal("\u001b", TerminalKeys.Key(Silk.NET.Input.Key.Escape, TerminalKeys.Modifiers.None, false, 1));
        Assert.Equal("\u001b[105;5u", TerminalKeys.Character("i", TerminalKeys.Modifiers.Control, 1));
        Assert.Equal("\u001bx", TerminalKeys.Character("x", TerminalKeys.Modifiers.Alt, 0));

        Assert.Equal("\u001b[<0;5;3M", TerminalKeys.Mouse(0, 4, 2, 0, true, false, true));
        Assert.Equal("\u001b[200~hi\u001b[201~", TerminalKeys.Paste("hi", true));
    }
}
