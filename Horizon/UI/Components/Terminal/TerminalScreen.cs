using System.Text;

namespace Horizon.UI.Components;

/// <summary>
/// The screen of a terminal, what the bytes a program writes come to. A grid of cells as many columns and rows as it
/// was given, a scrollback of the rows that went off the top, a cursor with a style it writes in, the margins and
/// the modes of a VT (origin, autowrap, insert, the application keys, the mouse, bracketed paste, the alternate
/// screen vim and htop draw on, which has no scrollback), tab stops, the character sets as far as DEC's line
/// drawing, the title, and the hyperlinks of OSC 8. What xterm does with a sequence this does, as far as a terminal
/// that is a terminal and not a printer needs, and what it has no use for it takes without a word. It is fed through
/// its <see cref="VtParser"/> (<see cref="Feed"/>), it keeps nothing of how it is drawn, and anything the program
/// asks it (where the cursor is, what it is) goes into <see cref="Responses"/> for whoever owns the process to send
/// back. Resizing reflows the lines that wrapped and keeps the ones that didn't, the way Windows Terminal does.
/// Everything here runs on the thread of whoever feeds it, a <see cref="TerminalView"/> feeds it on the UI's.
/// </summary>
public sealed partial class TerminalScreen : IVtHandler
{
    private const int MAX_SCROLLBACK_DEFAULT = 10000;

    /// <summary>How many rows of what went off the top are kept.</summary>
    public int MaxScrollback { get; set; } = MAX_SCROLLBACK_DEFAULT;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    /// <summary>The cursor, column and row from 0 on the grid, and whether a program wants it shown.</summary>
    public int CursorX { get; private set; }

    public int CursorY { get; private set; }

    public bool CursorVisible { get; private set; } = true;

    /// <summary>The style the next character is written in.</summary>
    public TerminalStyle Style { get; private set; } = TerminalStyle.Plain;

    /// <summary>What the program called its window, OSC 0 and 2.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Goes up by one every time anything on the screen changes, for a view to know when to draw again.</summary>
    public int Version { get; private set; }

    /// <summary>Whether the alternate screen is up, a full screen program's.</summary>
    public bool AlternateScreen { get; private set; }

    public bool ApplicationCursorKeys { get; private set; }

    public bool ApplicationKeypad { get; private set; }

    public bool BracketedPaste { get; private set; }

    /// <summary>Whether a program wants to hear about focus coming and going (mode 1004).</summary>
    public bool FocusEvents { get; private set; }

    /// <summary>What the program wants of the mouse, nothing, presses, presses and drags, or every move, and how it wants them written.</summary>
    public TerminalMouseMode MouseMode { get; private set; }

    public bool MouseSgr { get; private set; }

    /// <summary>The flags of the kitty keyboard protocol a program pushed (CSI > flags u), 0 for the old keys.</summary>
    public int KeyboardFlags => keyboardFlags.Count > 0 ? keyboardFlags.Peek() : 0;

    /// <summary>The rows of the scrollback, oldest first.</summary>
    public IReadOnlyList<TerminalRow> Scrollback => scrollback;

    /// <summary>The rows of the grid, top to bottom.</summary>
    public IReadOnlyList<TerminalRow> Grid => AlternateScreen ? alternate : main;

    /// <summary>What the program asked and is owed, the bytes to send it, taken by whoever owns the process.</summary>
    public StringBuilder Responses { get; } = new();

    /// <summary>Called on a bell, for a flash or a sound.</summary>
    public Action? Bell { get; set; }

    /// <summary>The targets of OSC 8 hyperlinks, by the id a cell's style carries, 0 unused.</summary>
    public IReadOnlyList<string> Links => links;

    private readonly VtParser parser;
    private readonly List<TerminalRow> scrollback = [];
    private List<TerminalRow> main = [], alternate = [];
    private readonly List<string> links = [string.Empty];
    private readonly Stack<int> keyboardFlags = new();

    // The scrolling region, inclusive rows, the modes that aren't properties, and the cursor saved by DECSC a screen each
    private int top, bottom;
    private bool originMode, autowrap = true, insertMode, pendingWrap, reverseVideo;
    private bool[] tabStops = [];
    private (int X, int Y, TerminalStyle Style, bool Origin, bool Pending) savedMain, savedAlternate;

    // G0 and G1 and which is in use, true for DEC line drawing
    private bool g0Graphics, g1Graphics, shiftOut;

    public TerminalScreen(int columns, int rows)
    {
        parser = new VtParser(this);
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);
        main = NewGrid(Columns, Rows);
        alternate = NewGrid(Columns, Rows);
        ResetTabs();
        top = 0;
        bottom = Rows - 1;
    }

    private static List<TerminalRow> NewGrid(int columns, int rows)
    {
        var grid = new List<TerminalRow>(rows);
        for (int i = 0; i < rows; i++)
            grid.Add(new TerminalRow(columns));
        return grid;
    }

    private List<TerminalRow> Current => AlternateScreen ? alternate : main;

    /// <summary>Reads bytes a program wrote.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        parser.Feed(bytes);
        if (LateMarks)
            lastFed = Environment.TickCount64;
        Version++;
    }

    /// <summary>Reads text as if a program wrote it, for a test.</summary>
    public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

    /// <summary>The cell at a column and row of the grid.</summary>
    public ref TerminalCell this[int x, int y] => ref Current[y].Cells[x];

    /// <summary>A row of everything, the scrollback then the grid, so index Scrollback.Count is the grid's top.</summary>
    public TerminalRow RowAt(int index) => index < scrollback.Count ? scrollback[index] : Current[index - scrollback.Count];

    public int TotalRows => scrollback.Count + Rows;

    /// <summary>The text of the grid, rows joined with newlines, trailing blanks dropped, for tests and copying.</summary>
    public string Text()
    {
        var text = new StringBuilder();
        for (int y = 0; y < Rows; y++)
        {
            if (y > 0) text.Append('\n');
            text.Append(Current[y].Text());
        }

        return text.ToString();
    }

    /* Size */

    /// <summary>
    /// Gives the screen a new size. The main screen's lines that wrapped are joined and wrapped again at the new
    /// width, the ones that didn't keep their breaks, and the cursor stays on its line. The alternate screen is cut
    /// or padded, a full screen program draws itself again when it hears.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
            return;

        // The main screen and its scrollback as one list of rows, reflowed, the cursor carried along
        var all = new List<TerminalRow>(scrollback.Count + Rows);
        all.AddRange(scrollback);
        all.AddRange(main);
        int cursorRow = scrollback.Count + CursorY;

        // Blank rows under the cursor on the grid are dropped, or every shrink leaves a trail of them in the scrollback
        int lastUsed = all.Count - 1;
        while (lastUsed > cursorRow && all[lastUsed].Used == 0 && !all[lastUsed - 1].Wrapped)
            lastUsed--;
        all.RemoveRange(lastUsed + 1, all.Count - lastUsed - 1);

        var reflowed = new List<TerminalRow>();
        int newCursorRow = 0, newCursorX = CursorX;
        for (int i = 0; i < all.Count;)
        {
            // One logical line, every row that wrapped into the next, its marks gathered for the first row it becomes
            int start = i;
            var cells = new List<TerminalCell>();
            TerminalRowMark marks = default;
            bool holdsLastPrompt = false;
            int cursorOffset = -1;

            // Where what is typed starts, as far along the whole line as it is, the mark's and the live one's. A prompt
            // that wrapped has it on its second row, and a column of that row is that much further along the line
            int markedInput = -1, liveInput = -1;
            while (true)
            {
                TerminalRow row = all[i];
                if (row.Mark.InputAt != 0 && markedInput < 0)
                    markedInput = cells.Count + row.Mark.InputAt - 1;
                if (ReferenceEquals(row, inputRow))
                    liveInput = cells.Count + inputColumn;
                marks = marks.With(row.Mark);
                holdsLastPrompt |= ReferenceEquals(row, lastPrompt);
                int used = row.Wrapped ? row.Length : row.Used;
                if (i == cursorRow)
                {
                    used = Math.Max(used, Math.Min(CursorX, row.Length));
                    cursorOffset = cells.Count + Math.Min(CursorX, used);
                }
                for (int c = 0; c < used; c++)
                    cells.Add(row.Cells[c]);
                i++;
                if (!row.Wrapped || i >= all.Count)
                    break;
            }

            // Wrapped again at the new width, a wide character never cut in two
            int at = 0;
            int firstOfLine = reflowed.Count;
            bool inputPlaced = markedInput < 0, livePlaced = liveInput < 0;
            do
            {
                int take = Math.Min(columns, cells.Count - at);
                if (take == columns && at + take < cells.Count && cells[at + take].IsContinuation)
                    take--;
                var row = new TerminalRow(columns);
                for (int c = 0; c < take; c++)
                    row.Cells[c] = cells[at + c];
                row.Wrapped = at + take < cells.Count;
                if (cursorOffset >= 0 && cursorOffset >= at && (cursorOffset < at + take || (!row.Wrapped && cursorOffset <= at + take)))
                {
                    newCursorRow = reflowed.Count;
                    newCursorX = Math.Min(columns - 1, cursorOffset - at);
                    cursorOffset = -1;
                }
                // The input goes on the row its place along the line is on now, the last row for one past the end
                bool last = at + take >= cells.Count;
                if (!inputPlaced && (markedInput < at + take || last))
                {
                    row.Mark.InputAt = Math.Clamp(markedInput - at, 0, columns - 1) + 1;
                    inputPlaced = true;
                }

                if (!livePlaced && (liveInput < at + take || last))
                {
                    inputRow = row;
                    inputColumn = Math.Clamp(liveInput - at, 0, columns - 1);
                    livePlaced = true;
                }

                reflowed.Add(row);
                at += take;
            } while (at < cells.Count);
            _ = start;

            // The rest of the marks are the first row's, the prompt starts there whatever it wrapped into
            TerminalRow first = reflowed[firstOfLine];
            int inputAt = first.Mark.InputAt;
            first.Mark = marks;
            first.Mark.InputAt = inputAt;
            if (holdsLastPrompt)
                lastPrompt = first;
        }

        // The grid is the last rows, the rest the scrollback, the cursor in sight
        Columns = columns;
        Rows = rows;
        scrollback.Clear();
        main = new List<TerminalRow>(rows);
        int firstOnGrid = Math.Max(0, Math.Min(reflowed.Count - rows, newCursorRow - rows + 1));
        firstOnGrid = Math.Max(firstOnGrid, Math.Max(0, newCursorRow - rows + 1));
        for (int r = 0; r < firstOnGrid; r++)
            scrollback.Add(reflowed[r]);
        for (int r = firstOnGrid; r < reflowed.Count; r++)
            main.Add(reflowed[r]);
        while (main.Count < rows)
            main.Add(new TerminalRow(columns));
        while (main.Count > rows)
        {
            scrollback.Add(main[0]);
            main.RemoveAt(0);
        }
        TrimScrollback();

        int cursorY = newCursorRow - firstOnGrid;
        if (!AlternateScreen)
        {
            CursorY = Math.Clamp(cursorY, 0, rows - 1);
            CursorX = Math.Clamp(newCursorX, 0, columns - 1);
        }

        // The alternate screen is cut or padded, and the cursor kept on it
        var resized = NewGrid(columns, rows);
        for (int y = 0; y < Math.Min(rows, alternate.Count); y++)
            Array.Copy(alternate[y].Cells, resized[y].Cells, Math.Min(columns, alternate[y].Length));
        alternate = resized;
        if (AlternateScreen)
        {
            CursorY = Math.Clamp(CursorY, 0, rows - 1);
            CursorX = Math.Clamp(CursorX, 0, columns - 1);
        }

        top = 0;
        bottom = rows - 1;
        pendingWrap = false;
        ResetTabs();
        Version++;
    }

    private void ResetTabs()
    {
        tabStops = new bool[Columns];
        for (int i = 8; i < Columns; i += 8)
            tabStops[i] = true;
    }

    private void TrimScrollback()
    {
        if (scrollback.Count > MaxScrollback)
            scrollback.RemoveRange(0, scrollback.Count - MaxScrollback);
    }

    /* Printing */

    public void Print(int codePoint)
    {
        if ((shiftOut ? g1Graphics : g0Graphics) && codePoint is >= 0x60 and <= 0x7E)
            codePoint = LineDrawing[codePoint - 0x60];

        int width = CharacterWidth(codePoint);
        List<TerminalRow> grid = Current;

        // A combining mark joins the character before it
        if (width == 0)
        {
            int x = CursorX - (pendingWrap ? 0 : 1);
            if (x >= 0 && x < Columns)
            {
                ref TerminalCell before = ref grid[CursorY].Cells[x];
                if (before.IsContinuation && x > 0)
                    before = ref grid[CursorY].Cells[x - 1];
                before.Combining = (before.Combining ?? string.Empty) + char.ConvertFromUtf32(codePoint);
            }
            return;
        }

        if (pendingWrap)
        {
            if (autowrap)
            {
                grid[CursorY].Wrapped = true;
                CursorX = 0;
                LineFeed();
            }
            else
            {
                CursorX = Columns - 1;
            }
            pendingWrap = false;
        }

        // A wide character that doesn't fit at the end goes to the next line, the cell it leaves is blank
        if (width == 2 && CursorX == Columns - 1)
        {
            grid[CursorY].Cells[CursorX] = TerminalCell.Erased(Style);
            if (autowrap)
            {
                grid[CursorY].Wrapped = true;
                CursorX = 0;
                LineFeed();
            }
            else
            {
                CursorX = Columns - 2;
            }
        }

        TerminalRow row = grid[CursorY];
        if (insertMode)
        {
            for (int c = Columns - 1; c >= CursorX + width; c--)
                row.Cells[c] = row.Cells[c - width];
        }

        // Writing over half of a wide character blanks the other half
        ClearWide(row, CursorX);
        if (width == 2)
            ClearWide(row, CursorX + 1);

        row.Cells[CursorX] = new TerminalCell { CodePoint = codePoint, Style = Style with { Attributes = Style.Attributes | (width == 2 ? TerminalAttributes.Wide : 0) } };
        if (width == 2)
            row.Cells[CursorX + 1] = new TerminalCell { CodePoint = 0, Style = Style with { Attributes = Style.Attributes | TerminalAttributes.Continuation } };

        CursorX += width;
        if (CursorX >= Columns)
        {
            CursorX = Columns - 1;
            pendingWrap = true;
        }
    }

    private static void ClearWide(TerminalRow row, int x)
    {
        if (x < 0 || x >= row.Length)
            return;

        if (row.Cells[x].IsContinuation && x > 0)
            row.Cells[x - 1] = TerminalCell.Erased(row.Cells[x - 1].Style);
        else if (row.Cells[x].Style.Has(TerminalAttributes.Wide) && x + 1 < row.Length)
            row.Cells[x + 1] = TerminalCell.Erased(row.Cells[x + 1].Style);
    }

    /// <summary>How many cells a character takes, 0 for a mark that combines, 2 for East Asian wide and emoji.</summary>
    public static int CharacterWidth(int codePoint)
    {
        if (codePoint == 0)
            return 1;
        if (codePoint < 0x300)
            return 1;

        // Combining marks and the zero width joiner and friends
        if (codePoint is (>= 0x300 and <= 0x36F) or (>= 0x483 and <= 0x489) or (>= 0x591 and <= 0x5BD) or (>= 0x610 and <= 0x61A) or (>= 0x64B and <= 0x65F)
            or (>= 0x1AB0 and <= 0x1AFF) or (>= 0x1DC0 and <= 0x1DFF) or (>= 0x200B and <= 0x200F) or (>= 0x20D0 and <= 0x20FF) or (>= 0xFE00 and <= 0xFE0F) or (>= 0xFE20 and <= 0xFE2F) or 0xFEFF
            or (>= 0xE0100 and <= 0xE01EF))
            return 0;

        // Wide, Hangul, CJK, full width forms, and the emoji blocks
        if (codePoint is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0x303E) or (>= 0x3041 and <= 0x33FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xA000 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3) or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6)
            or (>= 0x1F300 and <= 0x1F64F) or (>= 0x1F680 and <= 0x1F6FF) or (>= 0x1F900 and <= 0x1F9FF) or (>= 0x1FA70 and <= 0x1FAFF) or (>= 0x20000 and <= 0x3FFFD))
            return 2;

        return 1;
    }

    // DEC special graphics, ` through ~
    private static readonly int[] LineDrawing =
    [
        0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C, 0x2514, 0x253C, 0x23BA,
        0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7, 0x0020
    ];

    /* Controls */

    public void Execute(byte control)
    {
        switch (control)
        {
            case 0x07: Bell?.Invoke(); break;
            case 0x08: if (CursorX > 0) CursorX--; pendingWrap = false; break;
            case 0x09: Tab(); break;
            case 0x0A or 0x0B or 0x0C: LineFeed(); break;
            case 0x0D: CursorX = 0; pendingWrap = false; break;
            case 0x0E: shiftOut = true; break;
            case 0x0F: shiftOut = false; break;
        }
    }

    private void Tab()
    {
        pendingWrap = false;
        int x = CursorX + 1;
        while (x < Columns - 1 && !tabStops[x])
            x++;
        CursorX = Math.Min(x, Columns - 1);
    }

    private void LineFeed()
    {
        pendingWrap = false;
        if (CursorY == bottom)
            ScrollUp(1, top, bottom);
        else if (CursorY < Rows - 1)
            CursorY++;
    }

    private void ReverseLineFeed()
    {
        pendingWrap = false;
        if (CursorY == top)
            ScrollDown(1, top, bottom);
        else if (CursorY > 0)
            CursorY--;
    }

    // Rows out of the top of a region, the top row of the whole main screen going into the scrollback
    private void ScrollUp(int count, int from, int to)
    {
        List<TerminalRow> grid = Current;
        count = Math.Clamp(count, 0, to - from + 1);
        for (int i = 0; i < count; i++)
        {
            TerminalRow gone = grid[from];
            grid.RemoveAt(from);
            if (!AlternateScreen && from == 0)
            {
                scrollback.Add(gone);
                grid.Insert(to, new TerminalRow(Columns) { Cells = Blanks() });
            }
            else
            {
                gone.Wrapped = false;
                gone.Cells = Blanks();
                grid.Insert(to, gone);
            }
        }

        TrimScrollback();
    }

    private void ScrollDown(int count, int from, int to)
    {
        List<TerminalRow> grid = Current;
        count = Math.Clamp(count, 0, to - from + 1);
        for (int i = 0; i < count; i++)
        {
            TerminalRow gone = grid[to];
            grid.RemoveAt(to);
            gone.Wrapped = false;
            gone.Cells = Blanks();
            grid.Insert(from, gone);
        }
    }

    private TerminalCell[] Blanks()
    {
        var cells = new TerminalCell[Columns];
        Array.Fill(cells, TerminalCell.Erased(Style));
        return cells;
    }

    /* Escapes */

    public void Escape(ReadOnlySpan<byte> intermediates, byte final)
    {
        if (intermediates.Length == 1 && intermediates[0] is (byte)'(' or (byte)')')
        {
            bool graphics = final == (byte)'0';
            if (intermediates[0] == (byte)'(') g0Graphics = graphics; else g1Graphics = graphics;
            return;
        }

        if (intermediates.Length == 1 && intermediates[0] == (byte)'#' && final == (byte)'8')
        {
            // DECALN, the screen full of E, which vttest starts with
            foreach (TerminalRow row in Current)
                row.Fill(new TerminalCell { CodePoint = 'E', Style = TerminalStyle.Plain }, 0, Columns);
            return;
        }

        if (intermediates.Length > 0)
            return;

        switch ((char)final)
        {
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': LineFeed(); break;
            case 'E': CursorX = 0; LineFeed(); break;
            case 'H': tabStops[Math.Min(CursorX, Columns - 1)] = true; break;
            case 'M': ReverseLineFeed(); break;
            case '=': ApplicationKeypad = true; break;
            case '>': ApplicationKeypad = false; break;
            case 'c': Reset(); break;
        }
    }

    private void SaveCursor()
    {
        var saved = (CursorX, CursorY, Style, originMode, pendingWrap);
        if (AlternateScreen) savedAlternate = saved; else savedMain = saved;
    }

    private void RestoreCursor()
    {
        var (x, y, style, origin, pending) = AlternateScreen ? savedAlternate : savedMain;
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = Math.Clamp(y, 0, Rows - 1);
        Style = style;
        originMode = origin;
        pendingWrap = pending;
    }

    /// <summary>Everything back to how it started, RIS, with the scrollback kept.</summary>
    public void Reset()
    {
        main = NewGrid(Columns, Rows);
        alternate = NewGrid(Columns, Rows);
        MarksSeen = false;
        lastPrompt = inputRow = null;
        promptPending = inputPending = outputPending = false;
        exitPending = null;
        promptHeight = 1;
        promptArrivedAt = -1;
        promptLearned = false;
        AlternateScreen = false;
        CursorX = CursorY = 0;
        Style = TerminalStyle.Plain;
        CursorVisible = true;
        originMode = insertMode = pendingWrap = reverseVideo = false;
        autowrap = true;
        ApplicationCursorKeys = ApplicationKeypad = BracketedPaste = FocusEvents = false;
        MouseMode = TerminalMouseMode.None;
        MouseSgr = false;
        keyboardFlags.Clear();
        g0Graphics = g1Graphics = shiftOut = false;
        top = 0;
        bottom = Rows - 1;
        ResetTabs();
        Version++;
    }

    /* Control sequences */

    public void Csi(VtParams p, byte prefix, ReadOnlySpan<byte> intermediates, byte final)
    {
        if (prefix == (byte)'?')
        {
            switch ((char)final)
            {
                case 'h': for (int i = 0; i < Math.Max(1, p.Count); i++) PrivateMode(p[i], true); break;
                case 'l': for (int i = 0; i < Math.Max(1, p.Count); i++) PrivateMode(p[i], false); break;
                case 'u': Responses.Append("\u001b[?").Append(KeyboardFlags).Append('u'); break;
                case 'p' when intermediates.Length == 1 && intermediates[0] == (byte)'$':
                    // DECRQM, whether a private mode is set, 1 set, 2 reset, 0 for one this doesn't know
                    Responses.Append("\u001b[?").Append(p[0]).Append(';').Append(PrivateModeState(p[0])).Append("$y");
                    break;
                case 'n' when p[0] == 6: Responses.Append($"\u001b[?{CursorY + 1};{CursorX + 1}R"); break;
            }
            return;
        }

        if (prefix == (byte)'>')
        {
            switch ((char)final)
            {
                case 'u': keyboardFlags.Push(p[0]); break;
                case 'c': Responses.Append("\u001b[>1;10;0c"); break;
                case 'q': Responses.Append("\u001bP>|Horizon 1.0\u001b\\"); break;
            }
            return;
        }

        if (prefix == (byte)'<' && final == (byte)'u')
        {
            for (int i = 0; i < Math.Max(1, p.At(0, 1)) && keyboardFlags.Count > 0; i++)
                keyboardFlags.Pop();
            return;
        }

        if (prefix == (byte)'=' && final == (byte)'u')
        {
            int mode = p.At(1, 1), flags = p[0];
            int current = keyboardFlags.Count > 0 ? keyboardFlags.Pop() : 0;
            keyboardFlags.Push(mode switch { 2 => current | flags, 3 => current & ~flags, _ => flags });
            return;
        }

        if (prefix != 0)
            return;

        // DECRQM of an ANSI mode, insert (4) and new line (20) the only ones there are
        if (intermediates.Length == 1 && intermediates[0] == (byte)'$' && final == (byte)'p')
        {
            int state = p[0] switch { 4 => insertMode ? 1 : 2, 20 => 2, _ => 0 };
            Responses.Append("\u001b[").Append(p[0]).Append(';').Append(state).Append("$y");
            return;
        }

        if (intermediates.Length == 1 && intermediates[0] == (byte)' ' && final == (byte)'q')
        {
            CursorShape = p[0];
            return;
        }

        if (intermediates.Length > 0)
            return;

        int n = p.At(0, 1);
        switch ((char)final)
        {
            case '@': InsertCells(n); break;
            case 'A': MoveCursor(CursorX, CursorY - n); break;
            case 'B': MoveCursor(CursorX, CursorY + n); break;
            case 'C': MoveCursor(CursorX + n, CursorY); break;
            case 'D': MoveCursor(CursorX - n, CursorY); break;
            case 'E': MoveCursor(0, CursorY + n); break;
            case 'F': MoveCursor(0, CursorY - n); break;
            case 'G' or '`': MoveCursor(n - 1, CursorY); break;
            case 'H' or 'f': MoveCursorAbsolute(p.At(1, 1) - 1, p.At(0, 1) - 1); break;
            case 'I': for (int i = 0; i < n; i++) Tab(); break;
            case 'J': EraseInDisplay(p[0]); break;
            case 'K': EraseInLine(p[0]); break;
            case 'L': if (InRegion()) { ScrollDown(n, CursorY, bottom); CursorX = 0; } break;
            case 'M': if (InRegion()) { ScrollUp(n, CursorY, bottom); CursorX = 0; } break;
            case 'P': DeleteCells(n); break;
            case 'S': ScrollUp(n, top, bottom); break;
            case 'T': ScrollDown(n, top, bottom); break;
            case 'X': Current[CursorY].Fill(TerminalCell.Erased(Style), CursorX, CursorX + n); pendingWrap = false; break;
            case 'Z': for (int i = 0; i < n; i++) BackTab(); break;
            case 'b': break;
            case 'c': Responses.Append("\u001b[?62;22c"); break;
            case 'd': MoveCursorAbsolute(CursorX, n - 1, keepColumn: true); break;
            case 'g': if (p[0] == 3) Array.Clear(tabStops); else tabStops[Math.Min(CursorX, Columns - 1)] = false; break;
            case 'h': for (int i = 0; i < p.Count; i++) if (p[i] == 4) insertMode = true; break;
            case 'l': for (int i = 0; i < p.Count; i++) if (p[i] == 4) insertMode = false; break;
            case 'm': SetGraphics(p); break;
            case 'n': if (p[0] == 5) Responses.Append("\u001b[0n"); else if (p[0] == 6) Responses.Append($"\u001b[{(originMode ? CursorY - top : CursorY) + 1};{CursorX + 1}R"); break;
            case 'r':
                top = Math.Clamp(p.At(0, 1) - 1, 0, Rows - 1);
                bottom = Math.Clamp(p.At(1, Rows) - 1, 0, Rows - 1);
                if (bottom <= top) { top = 0; bottom = Rows - 1; }
                MoveCursorAbsolute(0, 0);
                break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't': break;
        }
    }

    /// <summary>The cursor's shape a program asked for with DECSCUSR, 0 and 1 a blinking block, 2 a block, 3 and 4 an underline, 5 and 6 a bar.</summary>
    public int CursorShape { get; private set; }

    private bool InRegion() => CursorY >= top && CursorY <= bottom;

    private void MoveCursor(int x, int y)
    {
        pendingWrap = false;
        int minY = originMode ? top : 0, maxY = originMode ? bottom : Rows - 1;
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = Math.Clamp(y, minY, maxY);
    }

    private void MoveCursorAbsolute(int x, int y, bool keepColumn = false)
    {
        pendingWrap = false;
        if (originMode)
            y += top;
        CursorX = Math.Clamp(x, 0, Columns - 1);
        CursorY = Math.Clamp(y, originMode ? top : 0, originMode ? bottom : Rows - 1);
        _ = keepColumn;
    }

    private void BackTab()
    {
        int x = CursorX - 1;
        while (x > 0 && !tabStops[x])
            x--;
        CursorX = Math.Max(0, x);
    }

    private void InsertCells(int count)
    {
        TerminalRow row = Current[CursorY];
        count = Math.Min(count, Columns - CursorX);
        for (int c = Columns - 1; c >= CursorX + count; c--)
            row.Cells[c] = row.Cells[c - count];
        row.Fill(TerminalCell.Erased(Style), CursorX, CursorX + count);
        pendingWrap = false;
    }

    private void DeleteCells(int count)
    {
        TerminalRow row = Current[CursorY];
        count = Math.Min(count, Columns - CursorX);
        for (int c = CursorX; c < Columns - count; c++)
            row.Cells[c] = row.Cells[c + count];
        row.Fill(TerminalCell.Erased(Style), Columns - count, Columns);
        pendingWrap = false;
    }

    private void EraseInDisplay(int mode)
    {
        List<TerminalRow> grid = Current;
        pendingWrap = false;
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int y = CursorY + 1; y < Rows; y++) { grid[y].Fill(TerminalCell.Erased(Style), 0, Columns); grid[y].Wrapped = false; ForgetMarks(grid[y]); }
                break;
            case 1:
                EraseInLine(1);
                for (int y = 0; y < CursorY; y++) { grid[y].Fill(TerminalCell.Erased(Style), 0, Columns); grid[y].Wrapped = false; ForgetMarks(grid[y]); }
                break;
            case 2:
                foreach (TerminalRow row in grid) { row.Fill(TerminalCell.Erased(Style), 0, Columns); row.Wrapped = false; ForgetMarks(row); }
                if (!AlternateScreen && grid.Count > 0)
                    grid[0].Mark.Cleared = true;
                break;
            case 3:
                // the scrollback's rows go and their marks with them, the prompt of the next exit code too if it was there
                if (lastPrompt is not null && !Current.Contains(lastPrompt))
                    lastPrompt = null;
                scrollback.Clear();
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        // The wrap that's pending is left pending, an erase doesn't move the cursor. ConPTY draws a full row, erases the
        // rest of it and carries on with the next row's text without moving the cursor, counting on the wrap still being
        // there. Cancelled, the next row's first letter went on the end of this one and everything after it sat a column
        // to the left, micro's status line and line numbers in a mess and its cursor said to be somewhere it wasn't
        TerminalRow row = Current[CursorY];
        switch (mode)
        {
            case 0: row.Fill(TerminalCell.Erased(Style), CursorX, Columns); row.Wrapped = false; break;
            case 1: row.Fill(TerminalCell.Erased(Style), 0, CursorX + 1); break;
            case 2: row.Fill(TerminalCell.Erased(Style), 0, Columns); row.Wrapped = false; break;
        }

        // A line erased whole is a line with nothing on it the marks could be about. Windows' ConPTY clears the screen
        // (cls, Clear-Host) by drawing it again from the top with every blank line erased this way, never ESC[2J
        if ((mode == 0 && CursorX == 0) || mode == 2)
            ForgetMarks(row);
    }

    /// <summary>
    /// Whether the program is in the middle of drawing a frame it asked to have shown whole (mode 2026), so whoever
    /// draws the screen waits for the end of it rather than showing half of one. A TUI's frame doesn't tear and the
    /// engine's blend of two ticks never mixes two of its frames.
    /// </summary>
    public bool SynchronizedOutput { get; private set; }

    /// <summary>A private mode as DECRQM answers it, 1 set, 2 reset, 0 for one this screen doesn't know.</summary>
    private int PrivateModeState(int mode) => mode switch
    {
        1 => ApplicationCursorKeys ? 1 : 2,
        5 => reverseVideo ? 1 : 2,
        6 => originMode ? 1 : 2,
        7 => autowrap ? 1 : 2,
        12 => 2,
        25 => CursorVisible ? 1 : 2,
        9 => MouseMode == TerminalMouseMode.X10 ? 1 : 2,
        1000 => MouseMode == TerminalMouseMode.Presses ? 1 : 2,
        1002 => MouseMode == TerminalMouseMode.Drags ? 1 : 2,
        1003 => MouseMode == TerminalMouseMode.Motion ? 1 : 2,
        1004 => FocusEvents ? 1 : 2,
        1006 => MouseSgr ? 1 : 2,
        47 or 1047 or 1049 => AlternateScreen ? 1 : 2,
        2004 => BracketedPaste ? 1 : 2,
        2026 => SynchronizedOutput ? 1 : 2,
        _ => 0
    };

    private void PrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: ApplicationCursorKeys = on; break;
            case 5: reverseVideo = on; break;
            case 6: originMode = on; MoveCursorAbsolute(0, 0); break;
            case 7: autowrap = on; break;
            case 12: break;
            case 25: CursorVisible = on; break;
            case 9: MouseMode = on ? TerminalMouseMode.X10 : TerminalMouseMode.None; break;
            case 1000: MouseMode = on ? TerminalMouseMode.Presses : TerminalMouseMode.None; break;
            case 1002: MouseMode = on ? TerminalMouseMode.Drags : TerminalMouseMode.None; break;
            case 1003: MouseMode = on ? TerminalMouseMode.Motion : TerminalMouseMode.None; break;
            case 1004: FocusEvents = on; break;
            case 1006: MouseSgr = on; break;
            case 1047: SwitchScreen(on, save: false, clear: true); break;
            case 47: SwitchScreen(on, save: false, clear: false); break;
            case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
            case 1049: SwitchScreen(on, save: true, clear: true); break;
            case 2004: BracketedPaste = on; break;
            case 2026: SynchronizedOutput = on; break;
        }
    }

    private void SwitchScreen(bool alternateOn, bool save, bool clear)
    {
        if (alternateOn == AlternateScreen)
            return;

        if (alternateOn)
        {
            if (save) SaveCursor();
            AlternateScreen = true;
            if (clear)
                alternate = NewGrid(Columns, Rows);
            CursorX = CursorY = 0;
        }
        else
        {
            AlternateScreen = false;
            if (save) RestoreCursor();
        }

        pendingWrap = false;
    }

    /// <summary>Whether the program asked for light on dark swapped, DECSCNM, a view draws it swapped.</summary>
    public bool ReverseVideo => reverseVideo;

    private void SetGraphics(VtParams p)
    {
        if (p.Count == 0)
        {
            Style = TerminalStyle.Plain with { Link = Style.Link };
            return;
        }

        TerminalStyle style = Style;
        for (int i = 0; i < p.Count; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0: style = TerminalStyle.Plain with { Link = style.Link }; break;
                case 1: style.Attributes |= TerminalAttributes.Bold; break;
                case 2: style.Attributes |= TerminalAttributes.Dim; break;
                case 3: style.Attributes |= TerminalAttributes.Italic; break;
                case 4:
                    style.Attributes |= TerminalAttributes.Underline;
                    style.Underline = TerminalUnderline.Single;
                    if (p.Sub(i + 1))
                    {
                        int kind = p[++i];
                        style.Underline = kind is >= 0 and <= 5 ? (TerminalUnderline)kind : TerminalUnderline.Single;
                        if (kind == 0) style.Attributes &= ~TerminalAttributes.Underline;
                    }
                    break;
                case 5 or 6: style.Attributes |= TerminalAttributes.Blink; break;
                case 7: style.Attributes |= TerminalAttributes.Inverse; break;
                case 8: style.Attributes |= TerminalAttributes.Invisible; break;
                case 9: style.Attributes |= TerminalAttributes.Strikethrough; break;
                case 21: style.Attributes |= TerminalAttributes.Underline; style.Underline = TerminalUnderline.Double; break;
                case 22: style.Attributes &= ~(TerminalAttributes.Bold | TerminalAttributes.Dim); break;
                case 23: style.Attributes &= ~TerminalAttributes.Italic; break;
                case 24: style.Attributes &= ~TerminalAttributes.Underline; style.Underline = TerminalUnderline.None; break;
                case 25: style.Attributes &= ~TerminalAttributes.Blink; break;
                case 27: style.Attributes &= ~TerminalAttributes.Inverse; break;
                case 28: style.Attributes &= ~TerminalAttributes.Invisible; break;
                case 29: style.Attributes &= ~TerminalAttributes.Strikethrough; break;
                case >= 30 and <= 37: style.Foreground = TerminalColor.Indexed(code - 30); break;
                case 38: style.Foreground = ExtendedColor(p, ref i) ?? style.Foreground; break;
                case 39: style.Foreground = TerminalColor.Default; break;
                case >= 40 and <= 47: style.Background = TerminalColor.Indexed(code - 40); break;
                case 48: style.Background = ExtendedColor(p, ref i) ?? style.Background; break;
                case 49: style.Background = TerminalColor.Default; break;
                case 53: style.Attributes |= TerminalAttributes.Overline; break;
                case 55: style.Attributes &= ~TerminalAttributes.Overline; break;
                case 58: style.UnderlineColor = ExtendedColor(p, ref i) ?? style.UnderlineColor; break;
                case 59: style.UnderlineColor = TerminalColor.Default; break;
                case >= 90 and <= 97: style.Foreground = TerminalColor.Indexed(code - 90 + 8); break;
                case >= 100 and <= 107: style.Background = TerminalColor.Indexed(code - 100 + 8); break;
            }
        }

        Style = style;
    }

    // 38;5;n, 38;2;r;g;b, and the colon forms 38:5:n and 38:2::r:g:b (with the colour space left empty)
    private static TerminalColor? ExtendedColor(VtParams p, ref int i)
    {
        if (i + 1 >= p.Count)
            return null;

        bool colon = p.Sub(i + 1);
        int kind = p[i + 1];
        if (kind == 5 && i + 2 < p.Count)
        {
            i += 2;
            return TerminalColor.Indexed(p[i]);
        }

        if (kind == 2)
        {
            // With colons the colour space id may sit between the 2 and the red, 38:2::r:g:b, which is three subs before r
            int at = i + 2;
            if (colon && i + 5 < p.Count && p.Sub(i + 5) && p[i + 2] == 0 && p.Count > i + 5)
                at = i + 3;
            if (at + 2 >= p.Count)
                return null;
            var color = TerminalColor.Rgb(p[at], p[at + 1], p[at + 2]);
            i = at + 2;
            return color;
        }

        return null;
    }

    /* Strings */

    /// <summary>
    /// The private string a shell says to ask the program round the terminal for something, OSC 1717. The shells'
    /// integration has the dawn command say <c>dawn;inline;C:\path</c> with it (dusk.ps1, dusk.bash, dawn.cmd), see
    /// <see cref="TryTakeRequest"/>.
    /// </summary>
    public const int REQUEST_OSC = 1717;

    // What the shell asked for that nobody has taken yet, a few at the most, a program that spams it loses the rest
    private readonly Queue<string> requests = new();
    private const int MOST_REQUESTS = 16;

    /// <summary>The next thing the shell asked the program round the terminal for (see <see cref="REQUEST_OSC"/>), false when there's none.</summary>
    public bool TryTakeRequest(out string request) => requests.TryDequeue(out request!);

    public void Osc(ReadOnlySpan<char> text)
    {
        int semicolon = text.IndexOf(';');
        if (!int.TryParse(semicolon < 0 ? text : text[..semicolon], out int command))
            return;
        ReadOnlySpan<char> rest = semicolon < 0 ? [] : text[(semicolon + 1)..];

        switch (command)
        {
            case 0 or 2:
                Title = rest.ToString();
                break;

            case 133:
                Mark(rest);
                break;

            case REQUEST_OSC:
                if (requests.Count < MOST_REQUESTS && !rest.IsEmpty)
                    requests.Enqueue(rest.ToString());
                break;

            case 9 when rest.StartsWith("9;"):
                // ConEmu's and Windows Terminal's way of saying the folder, 9;9;C:\path, the one cmd's prompt can say
                if (rest[2..].Trim('"') is { Length: > 0 } said)
                    WorkingDirectory = said.ToString();
                break;

            case 7:
                // The folder the shell is in, file://host/path, which shells with integration say at every prompt
                WorkingDirectory = FolderOf(rest.ToString()) ?? WorkingDirectory;
                break;

            case 8:
            {
                // 8;params;url, an empty url ends the link
                int split = rest.IndexOf(';');
                ReadOnlySpan<char> url = split < 0 ? [] : rest[(split + 1)..];
                if (url.IsEmpty)
                {
                    Style = Style with { Link = 0 };
                }
                else
                {
                    string target = url.ToString();
                    int id = links.IndexOf(target);
                    if (id < 0)
                    {
                        if (links.Count < ushort.MaxValue)
                        {
                            links.Add(target);
                            id = links.Count - 1;
                        }
                        else
                        {
                            id = 0;
                        }
                    }
                    Style = Style with { Link = (ushort)id };
                }
                break;
            }

            // The colours asked for, as the palette the screen is drawn in has them (vim and neovim ask for the background
            // to know whether it is dark), the foreground, the background, the cursor and any of the 256
            case 10 when rest is "?":
                Responses.Append("\u001b]10;").Append(Rgb(Palette.Foreground)).Append("\u001b\\");
                break;
            case 11 when rest is "?":
                Responses.Append("\u001b]11;").Append(Rgb(Palette.Background)).Append("\u001b\\");
                break;
            case 12 when rest is "?":
                Responses.Append("\u001b]12;").Append(Rgb(Palette.Cursor)).Append("\u001b\\");
                break;
            case 4 when rest.EndsWith(";?") && int.TryParse(rest[..rest.IndexOf(';')], out int index) && index is >= 0 and < 256:
                Responses.Append("\u001b]4;").Append(index).Append(';').Append(Rgb(Palette.Of(TerminalColor.Indexed((byte)index), true))).Append("\u001b\\");
                break;

            // The clipboard, set by the program (a yank in vim over SSH), only when whoever draws the screen lets it. Asked
            // for, it is never answered, a program reading the clipboard is a program reading whatever was copied last
            case 52 when rest.IndexOf(';') is var separator and >= 0 && rest[(separator + 1)..] is var data && !data.SequenceEqual("?"):
                try
                {
                    Clipboard?.Invoke(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data.ToString())));
                }
                catch (FormatException)
                {
                    // Not base 64, nothing set
                }
                break;
        }
    }

    /// <summary>
    /// Everything there is, the scrollback and the grid, a line a line as the program wrote them, a row that wrapped
    /// joined to the one after it. What a search or a test reads, a word the edge cut in two is one word here.
    /// </summary>
    public string AllText()
    {
        var text = new StringBuilder();
        for (int row = 0; row < TotalRows; row++)
        {
            TerminalRow line = RowAt(row);
            if (!line.Wrapped)
            {
                text.Append(line.Text().TrimEnd()).Append('\n');
                continue;
            }

            // A row that wrapped was written to its edge, the blanks at its end are spaces the program wrote
            text.Append(line.Text());
            int blanks = 0;
            for (int cell = Math.Min(Columns, line.Cells.Length) - 1; cell >= 0 && line.Cells[cell].IsBlank; cell--)
                blanks++;
            text.Append(' ', blanks);
        }

        return text.ToString();
    }

    /// <summary>The folder the program said it is in (OSC 7), null until it has.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>The folder out of an OSC 7 file URL, file://host/C:/path or file:///home/me, null for one that isn't.</summary>
    public static string? FolderOf(string url)
    {
        if (!url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return null;

        string rest = url[7..];
        int slash = rest.IndexOf('/');
        if (slash < 0)
            return null;

        string path = Uri.UnescapeDataString(rest[slash..]);

        // file:///C:/Users is /C:/Users here, the slash before the drive is the URL's
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            path = path[1..].Replace('/', '\\');
        return path.Length > 0 ? path : null;
    }

    /// <summary>The colours the screen is drawn in, what a program that asks for them is told. The view hands its own over.</summary>
    public TerminalPalette Palette { get; set; } = new();

    /// <summary>The program set the clipboard (OSC 52), with the text, for whoever draws the screen to allow or not.</summary>
    public Action<string>? Clipboard { get; set; }

    private static string Rgb(System.Numerics.Vector4 colour)
    {
        static string Channel(float value) => ((int)(Math.Clamp(value, 0.0f, 1.0f) * 255.0f + 0.5f)).ToString("x2") is var two ? two + two : "0000";
        return $"rgb:{Channel(colour.X)}/{Channel(colour.Y)}/{Channel(colour.Z)}";
    }

    public void Dcs(VtParams parameters, ReadOnlySpan<byte> intermediates, byte final, ReadOnlySpan<byte> body)
    {
        // DECRQSS, a setting asked for, the cursor's shape (SP q), the rendition (m) and the margins (r). Anything else
        // is said to be unknown. XTGETTCAP and sixels and the rest of what a DCS carries are taken and dropped
        if (intermediates.Length == 1 && intermediates[0] == (byte)'$' && final == (byte)'q')
        {
            string asked = System.Text.Encoding.ASCII.GetString(body);
            string? answer = asked switch
            {
                " q" => $"{(CursorShape == 0 ? 1 : CursorShape)} q",
                "m" => "0m",
                "r" => $"{top + 1};{bottom + 1}r",
                _ => null
            };
            Responses.Append(answer is null ? "\u001bP0$r\u001b\\" : $"\u001bP1$r{answer}\u001b\\");
        }
    }
}

/// <summary>What a program asked of the mouse, in the order each takes in the one before it.</summary>
public enum TerminalMouseMode { None, X10, Presses, Drags, Motion }
