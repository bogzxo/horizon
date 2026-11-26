using System.Text;

using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// Headless correctness and regression tests for Pass 5 Horizon.Terminal fixes:
/// TerminalScreen resize CursorY &amp; wide-character orphan clearing, VtParser UTF-8/ST/ASCII fast path,
/// TerminalView Arrived+Ended drain ordering, and TerminalKeys non-BMP Kitty &amp; X10 mouse bounds.
/// </summary>
public sealed class Pass5TerminalTests
{
    private static void AssertNoOrphanedWideCells(TerminalScreen screen)
    {
        for (int y = 0; y < screen.Rows; y++)
        {
            TerminalRow row = screen.Grid[y];
            for (int x = 0; x < row.Length; x++)
            {
                bool isWide = row.Cells[x].Style.Has(TerminalAttributes.Wide);
                bool isCont = row.Cells[x].IsContinuation;
                Assert.False(isWide && isCont, $"Cell ({x},{y}) cannot be both Wide and Continuation.");
                if (isWide)
                {
                    Assert.True(x + 1 < row.Length, $"Orphaned Wide cell at right edge ({x},{y}).");
                    Assert.True(row.Cells[x + 1].IsContinuation, $"Wide cell at ({x},{y}) is missing its Continuation partner.");
                }
                if (isCont)
                {
                    Assert.True(x > 0, $"Orphaned Continuation cell at left edge (0,{y}).");
                    Assert.True(row.Cells[x - 1].Style.Has(TerminalAttributes.Wide), $"Continuation cell at ({x},{y}) is missing its Wide partner.");
                }
            }
        }
    }

    /* TerminalScreen.Resize CursorY */

    [Fact]
    public void Resize_shrink_preserves_cursor_row_when_non_blank_lines_exist_below()
    {
        var screen = new TerminalScreen(10, 5);
        screen.Feed("line0\r\nline1\r\nline2\r\nline3\r\nline4");
        // Move cursor up to row 3 ("line3"), col 2
        screen.Feed("\u001b[4;3H");
        Assert.Equal((2, 3), (screen.CursorX, screen.CursorY));

        // Shrink to 3 rows: line0 and line1 move to scrollback, grid holds line2, line3, line4
        screen.Resize(10, 3);
        Assert.Equal(2, screen.Scrollback.Count);
        Assert.Equal("line2\nline3\nline4", screen.Text());
        Assert.Equal((2, 1), (screen.CursorX, screen.CursorY));
    }

    [Fact]
    public void Resize_shrink_clamps_cursor_y_when_cursor_scrolls_above_grid()
    {
        var screen = new TerminalScreen(10, 5);
        screen.Feed("line0\r\nline1\r\nline2\r\nline3\r\nline4");
        // Move cursor to top row ("line0")
        screen.Feed("\u001b[1;4H");
        Assert.Equal((3, 0), (screen.CursorX, screen.CursorY));

        screen.Resize(10, 2);
        Assert.Equal(3, screen.Scrollback.Count);
        Assert.Equal("line3\nline4", screen.Text());
        Assert.Equal(0, screen.CursorY);
        Assert.InRange(screen.CursorY, 0, screen.Rows - 1);
    }

    [Fact]
    public void Resize_on_alternate_screen_clamps_alternate_cursor_and_preserves_saved_main_cursor()
    {
        var screen = new TerminalScreen(10, 5);
        screen.Feed("a\r\nb\r\nc\r\nd\r\ne\u001b[4;3H");
        screen.Feed("\u001b[?1049h\u001b[5;9H中");
        // Alternate screen cursor is at bottom-right; shrink both dimensions
        screen.Resize(5, 3);
        Assert.True(screen.AlternateScreen);
        Assert.InRange(screen.CursorX, 0, 4);
        Assert.InRange(screen.CursorY, 0, 2);
        AssertNoOrphanedWideCells(screen);

        // Leave alternate screen: restored main cursor should stay valid and on "d" (row 1 of c,d,e)
        screen.Feed("\u001b[?1049l");
        Assert.False(screen.AlternateScreen);
        Assert.Equal((2, 1), (screen.CursorX, screen.CursorY));
    }

    /* Wide-character orphan clearing */

    [Fact]
    public void EraseInLine_clears_orphaned_wide_character_halves()
    {
        // EL 0 starting on continuation half of '中' (cols 1..2)
        var screen = new TerminalScreen(8, 2);
        screen.Feed("a中b");
        screen.Feed("\u001b[1;3H\u001b[0K"); // col index 2 = continuation half
        AssertNoOrphanedWideCells(screen);
        Assert.Equal(' ', screen[1, 0].CodePoint);
        Assert.Equal('a', screen[0, 0].CodePoint);

        // EL 1 ending on wide half of '中' (cols 1..2)
        screen = new TerminalScreen(8, 2);
        screen.Feed("a中b");
        screen.Feed("\u001b[1;2H\u001b[1K"); // col index 1 = wide half
        AssertNoOrphanedWideCells(screen);
        Assert.Equal(' ', screen[2, 0].CodePoint);
        Assert.Equal('b', screen[3, 0].CodePoint);
    }

    [Fact]
    public void EraseCharacters_clears_orphaned_halves_on_both_boundaries()
    {
        // "中中" occupies cols [0,1] and [2,3]
        var screen = new TerminalScreen(8, 2);
        screen.Feed("中中");
        // ECH 2 at col 1 erases cols [1,2]: splits left '中' (col 0) and right '中' (col 3)
        screen.Feed("\u001b[1;2H\u001b[2X");
        AssertNoOrphanedWideCells(screen);
        for (int x = 0; x < 4; x++)
            Assert.Equal(' ', screen[x, 0].CodePoint);
    }

    [Fact]
    public void EraseInDisplay_clears_orphaned_wide_character_halves()
    {
        var screen = new TerminalScreen(8, 2);
        screen.Feed("a中b\r\ncd");
        screen.Feed("\u001b[1;3H\u001b[0J"); // col 2 = continuation half
        AssertNoOrphanedWideCells(screen);
        Assert.Equal("a\n", screen.Text());

        screen = new TerminalScreen(8, 2);
        screen.Feed("ab\r\nc中d");
        screen.Feed("\u001b[2;2H\u001b[1J"); // col 1 = wide half on row 1
        AssertNoOrphanedWideCells(screen);
        Assert.Equal("\n   d", screen.Text());
    }

    [Fact]
    public void InsertBlankCharacters_clears_split_wide_char_and_right_edge_orphan()
    {
        // Width 5: "a中中" -> a at 0, 中 at [1,2], 中 at [3,4]
        var screen = new TerminalScreen(5, 2);
        screen.Feed("a中中");
        // ICH 1 at col 2 (continuation of first 中): splits first 中 and shifts second 中 so its wide half hits col 4
        screen.Feed("\u001b[1;3H\u001b[1@");
        AssertNoOrphanedWideCells(screen);
        Assert.Equal('a', screen[0, 0].CodePoint);
        Assert.Equal(' ', screen[1, 0].CodePoint);
        Assert.Equal(' ', screen[4, 0].CodePoint);
    }

    [Fact]
    public void DeleteCharacters_clears_orphaned_halves_on_both_boundaries()
    {
        // "中中x" -> 中 at [0,1], 中 at [2,3], x at 4
        var screen = new TerminalScreen(6, 2);
        screen.Feed("中中x");
        // DCH 2 at col 1 deletes cols [1,2]: left boundary splits col 0, right boundary splits col 3
        screen.Feed("\u001b[1;2H\u001b[2P");
        AssertNoOrphanedWideCells(screen);
        Assert.Equal(' ', screen[0, 0].CodePoint);
        Assert.Equal(' ', screen[1, 0].CodePoint);
        Assert.Equal('x', screen[2, 0].CodePoint);
    }

    [Fact]
    public void Wide_character_overwrite_at_right_edge_and_insert_mode_never_leave_orphans()
    {
        // Width 4: "ab中" -> a=0, b=1, 中=[2,3]. Move to col 3 (continuation) and print '漢' (wide) -> wraps to next row, must clear col 2
        var screen = new TerminalScreen(4, 2);
        screen.Feed("ab中\u001b[1;4H漢");
        AssertNoOrphanedWideCells(screen);
        Assert.Equal("ab\n漢", screen.Text());

        // Insert mode at continuation half
        screen = new TerminalScreen(5, 2);
        screen.Feed("a中b\u001b[4h\u001b[1;3HZ");
        AssertNoOrphanedWideCells(screen);
        Assert.Equal(' ', screen[1, 0].CodePoint);
        Assert.Equal('Z', screen[2, 0].CodePoint);
    }

    [Fact]
    public void Row_generation_increments_only_on_mutated_rows()
    {
        var screen = new TerminalScreen(10, 3);
        int g0 = screen.Grid[0].Generation;
        int g1 = screen.Grid[1].Generation;
        int g2 = screen.Grid[2].Generation;

        screen.Feed("hello");
        Assert.True(screen.Grid[0].Generation > g0);
        Assert.Equal(g1, screen.Grid[1].Generation);
        Assert.Equal(g2, screen.Grid[2].Generation);
    }

    /* VtParser: UTF-8 validation, ST across chunks, ASCII ground fast path */

    private sealed class RecordingVtHandler : IVtHandler
    {
        public readonly List<int> Printed = [];
        public readonly List<byte> Executed = [];
        public readonly List<(byte[] Intermediates, byte Final)> Escapes = [];
        public readonly List<string> Oscs = [];
        public readonly List<(byte Final, byte[] Body)> Dcss = [];
        public int PrintAsciiCalls;

        public void Print(int codePoint) => Printed.Add(codePoint);

        public void PrintAscii(ReadOnlySpan<byte> ascii)
        {
            PrintAsciiCalls++;
            foreach (byte b in ascii)
                Printed.Add(b);
        }

        public void Execute(byte control) => Executed.Add(control);
        public void Escape(ReadOnlySpan<byte> intermediates, byte final) => Escapes.Add((intermediates.ToArray(), final));
        public void Csi(VtParams parameters, byte prefix, ReadOnlySpan<byte> intermediates, byte final) { }
        public void Osc(ReadOnlySpan<char> text) => Oscs.Add(text.ToString());
        public void Dcs(VtParams parameters, ReadOnlySpan<byte> intermediates, byte final, ReadOnlySpan<byte> body) => Dcss.Add((final, body.ToArray()));
    }

    [Fact]
    public void VtParser_rejects_overlong_surrogate_and_out_of_range_utf8_and_reprocesses_interrupting_bytes()
    {
        var handler = new RecordingVtHandler();
        var parser = new VtParser(handler);

        // Overlong 2-byte '/' (0xC0 0xAF), overlong 3-byte (0xE0 0x80 0xAF), overlong 4-byte (0xF0 0x80 0x80 0xAF)
        parser.Feed([0xC0, 0xAF, 0xE0, 0x80, 0xAF, 0xF0, 0x80, 0x80, 0xAF]);
        Assert.All(handler.Printed, cp => Assert.Equal(0xFFFD, cp));
        Assert.DoesNotContain((int)'/', handler.Printed);

        // UTF-16 surrogate U+D800 (0xED 0xA0 0x80) and > U+10FFFF (0xF4 0x90 0x80 0x80)
        handler.Printed.Clear();
        parser.Feed([0xED, 0xA0, 0x80, 0xF4, 0x90, 0x80, 0x80]);
        Assert.All(handler.Printed, cp => Assert.Equal(0xFFFD, cp));

        // Truncated 3-byte sequence (0xE2 0x82) interrupted by ASCII 'A' and then ESC [ 3 1 m 'B'
        handler.Printed.Clear();
        parser.Feed([0xE2, 0x82, (byte)'A', 0xE2, 0x1B, (byte)'[', (byte)'3', (byte)'1', (byte)'m', (byte)'B']);
        Assert.Equal([0xFFFD, 'A', 0xFFFD, 'B'], handler.Printed);

        // Valid boundary scalar values: U+0800 (0xE0 0xA0 0x80), U+D7FF (0xED 0x9F 0xBF), U+10FFFF (0xF4 0x8F 0xBF 0xBF)
        handler.Printed.Clear();
        parser.Feed([0xE0, 0xA0, 0x80, 0xED, 0x9F, 0xBF, 0xF4, 0x8F, 0xBF, 0xBF]);
        Assert.Equal([0x0800, 0xD7FF, 0x10FFFF], handler.Printed);
    }

    [Fact]
    public void VtParser_handles_7bit_and_C1_string_terminators_across_chunks_for_OSC_DCS_and_SOS_PM_APC()
    {
        var handler = new RecordingVtHandler();
        var parser = new VtParser(handler);

        // OSC with UTF-8 'Ü' (0xC3 0x9C must NOT terminate early!) ended by split 7-bit ESC \
        parser.Feed([0x1B, (byte)']', (byte)'0', (byte)';', 0xC3, 0x9C, 0x1B]);
        parser.Feed([(byte)'\\', (byte)'A']);
        Assert.Single(handler.Oscs);
        Assert.Equal("0;Ü", handler.Oscs[0]);
        Assert.Empty(handler.Escapes);
        Assert.Equal([(int)'A'], handler.Printed);

        // OSC ended by C1 ST (0x9C) across chunk boundary
        handler.Oscs.Clear();
        handler.Printed.Clear();
        parser.Feed([0x1B, (byte)']', (byte)'2', (byte)';', (byte)'o', (byte)'k']);
        parser.Feed([0x9C, (byte)'B']);
        Assert.Equal(["2;ok"], handler.Oscs);
        Assert.Equal([(int)'B'], handler.Printed);

        // DCS ended by split 7-bit ESC \ (must NOT emit spurious Escape('\\'))
        handler.Printed.Clear();
        parser.Feed([0x1B, (byte)'P', (byte)'$', (byte)'q', (byte)'r', 0x1B]);
        parser.Feed([(byte)'\\', (byte)'C']);
        Assert.Single(handler.Dcss);
        Assert.Equal((byte)'q', handler.Dcss[0].Final);
        Assert.Equal([(byte)'r'], handler.Dcss[0].Body);
        Assert.Empty(handler.Escapes);
        Assert.Equal([(int)'C'], handler.Printed);

        // DCS ended by C1 ST (0x9C)
        handler.Dcss.Clear();
        handler.Printed.Clear();
        parser.Feed([0x1B, (byte)'P', (byte)'q', (byte)'x', (byte)'y']);
        parser.Feed([0x9C, (byte)'D']);
        Assert.Single(handler.Dcss);
        Assert.Equal([(byte)'x', (byte)'y'], handler.Dcss[0].Body);
        Assert.Equal([(int)'D'], handler.Printed);

        // SOS (ESC X), PM (ESC ^), APC (ESC _) ended by split ESC \ and C1 0x9C
        handler.Printed.Clear();
        parser.Feed([0x1B, (byte)'_', (byte)'i', (byte)'g', (byte)'n', (byte)'o', (byte)'r', (byte)'e', 0x1B]);
        parser.Feed([(byte)'\\', (byte)'E']);
        parser.Feed([0x1B, (byte)'^', (byte)'p', (byte)'m', 0x9C, (byte)'F']);
        parser.Feed([0x1B, (byte)'X', (byte)'s', (byte)'o', (byte)'s', 0x1B, (byte)'\\', (byte)'G']);
        Assert.Empty(handler.Escapes);
        Assert.Equal([(int)'E', (int)'F', (int)'G'], handler.Printed);
    }

    [Fact]
    public void VtParser_bulk_ascii_ground_fast_path_preserves_wrapping_and_transitions()
    {
        var handler = new RecordingVtHandler();
        var parser = new VtParser(handler);
        parser.Feed("HelloWorld\u001b[0m€Tail"u8);

        Assert.True(handler.PrintAsciiCalls >= 2);
        Assert.Equal("HelloWorld€Tail", string.Concat(handler.Printed.Select(char.ConvertFromUtf32)));

        // Screen integration with wrapping across bulk ASCII run
        var screen = new TerminalScreen(5, 3);
        screen.Feed("abcdefghijkl"u8);
        Assert.Equal("abcde\nfghij\nkl", screen.Text());
        Assert.Equal((2, 2), (screen.CursorX, screen.CursorY));
    }

    /* TerminalView Arrived + Ended drain ordering */

    [Fact]
    public void TerminalView_drains_all_arrived_and_held_bytes_before_transitioning_to_ended()
    {
        using var view = new TerminalView();
        string? textAtExit = null;
        int? exitCodeSeen = null;
        view.OnExited = code =>
        {
            exitCodeSeen = code;
            textAtExit = view.Screen.Text();
        };

        // Enqueue synchronized output + trailing output, then signal exit before Update runs
        view.Arrived(Encoding.UTF8.GetBytes("\u001b[?2026hhello "));
        view.Arrived(Encoding.UTF8.GetBytes("world"));
        view.Ended(42);

        view.UpdateTree(0.016f);

        Assert.Equal(42, exitCodeSeen);
        Assert.Equal(42, view.EndedWith);
        Assert.NotNull(textAtExit);
        Assert.StartsWith("hello world", textAtExit);
    }

    [Fact]
    public void TerminalView_handles_zero_and_negative_exit_codes_atomically()
    {
        using var view = new TerminalView();
        int? exitCodeSeen = null;
        view.OnExited = code => exitCodeSeen = code;

        view.Arrived("bye"u8.ToArray());
        view.Ended(0);
        view.UpdateTree(0.016f);
        Assert.Equal(0, exitCodeSeen);
        Assert.Equal(0, view.EndedWith);

        exitCodeSeen = null;
        view.Ended(-1);
        view.UpdateTree(0.016f);
        Assert.Equal(-1, exitCodeSeen);
        Assert.Equal(-1, view.EndedWith);
    }

    /* TerminalKeys: non-BMP Kitty & X10 mouse bounds */

    [Fact]
    public void TerminalKeys_Character_encodes_non_bmp_scalar_code_points_in_kitty_protocol()
    {
        // Emoji U+1F600 (128512) with Ctrl in Kitty mode
        Assert.Equal("\u001b[128512;5u", TerminalKeys.Character("😀", TerminalKeys.Modifiers.Control, 1));
        // Deseret capital letter U+10400 -> lowercase U+10428 (66600) with Alt in Kitty mode
        Assert.Equal("\u001b[66600;3u", TerminalKeys.Character("\U00010400", TerminalKeys.Modifiers.Alt, 1));
        // Lone high surrogate must not throw
        string lone = "\uD800";
        Assert.Equal("\u001b" + lone, TerminalKeys.Character(lone, TerminalKeys.Modifiers.Alt, 1));
    }

    [Fact]
    public void TerminalKeys_Mouse_clamps_X10_coordinates_and_button_without_byte_overflow()
    {
        // Boundary coords: 95 (byte 128), 222 (byte 255), > 222 (clamped to 255), negative (clamped to 33)
        string at95 = TerminalKeys.Mouse(0, 95, 95, TerminalKeys.Modifiers.None, pressed: true, motion: false, sgr: false);
        Assert.Equal(6, at95.Length);
        Assert.Equal((char)128, at95[4]);
        Assert.Equal((char)128, at95[5]);

        string at222 = TerminalKeys.Mouse(0, 222, 222, TerminalKeys.Modifiers.None, pressed: true, motion: false, sgr: false);
        Assert.Equal((char)255, at222[4]);
        Assert.Equal((char)255, at222[5]);

        string overflow = TerminalKeys.Mouse(500, 300, int.MaxValue, TerminalKeys.Modifiers.Control | TerminalKeys.Modifiers.Shift, pressed: true, motion: true, sgr: false);
        Assert.Equal(6, overflow.Length);
        Assert.Equal((char)255, overflow[3]);
        Assert.Equal((char)255, overflow[4]);
        Assert.Equal((char)255, overflow[5]);

        string negative = TerminalKeys.Mouse(-10, -5, -1, TerminalKeys.Modifiers.None, pressed: true, motion: false, sgr: false);
        Assert.Equal(6, negative.Length);
        Assert.Equal((char)32, negative[3]);
        Assert.Equal((char)33, negative[4]);
        Assert.Equal((char)33, negative[5]);
    }

    /* TerminalProfiles caching */

    [Fact]
    public void TerminalProfiles_Discover_caches_and_returns_isolated_copies()
    {
        TerminalProfiles.InvalidateCache();
        List<TerminalProfile> first = TerminalProfiles.Discover();
        List<TerminalProfile> second = TerminalProfiles.Discover();

        Assert.NotSame(first, second);
        Assert.Equal(first.Select(p => p.Id), second.Select(p => p.Id));

        if (first.Count > 0)
        {
            string originalName = first[0].Name;
            first[0].Name = "Mutated";
            first.Clear();

            List<TerminalProfile> third = TerminalProfiles.Discover();
            Assert.NotEmpty(third);
            Assert.Equal(originalName, third[0].Name);
        }
    }
}
