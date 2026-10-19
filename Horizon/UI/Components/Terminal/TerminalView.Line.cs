using System.Numerics;

namespace Horizon.UI.Components;

// The line typed at a prompt, coloured as it is typed. The colours are laid over the cells at paint and never written
// into the screen, so the shell's own come back the moment the line is run. A command's colour (green there, red not)
// goes over whatever the shell said, the rest only over cells the shell left plain, PSReadLine colours its line already
// and better, and a path that is there is underlined either way.
public partial class TerminalView
{
    /// <summary>What colours the line typed at a prompt, null to leave it alone.</summary>
    public ITerminalLineColourer? LineColourer
    {
        get => lineColourer;
        set
        {
            lineColourer = value;
            colouredVersion = -1;
            lineRow = -1;
        }
    }

    private ITerminalLineColourer? lineColourer;

    // The line as it was read off the screen, a character a cell from the end of the prompt, and what each is. Kept and
    // grown, never made anew, it is read again every time the screen changes at a prompt
    private char[] lineText = new char[256];
    private TerminalLineKind[] lineKinds = new TerminalLineKind[256];
    private int lineRow = -1, lineColumn, lineLength, lineStride, colouredVersion = -1, colouredBy;

    /// <summary>Where the typed line starts (a row of the whole and a column) and how long it is, null while there is none coloured.</summary>
    public (int Row, int Column, int Length)? Line => lineRow < 0 ? null : (lineRow, lineColumn, lineLength);

    /// <summary>What a character of the coloured line is, None past its end, for a test.</summary>
    public TerminalLineKind LineKindAt(int index) => lineRow >= 0 && index >= 0 && index < lineLength ? lineKinds[index] : TerminalLineKind.None;

    /// <summary>The coloured line's text, for a test.</summary>
    public string LineText => lineRow < 0 ? string.Empty : new string(lineText, 0, lineLength);

    /// <summary>The kinds of the coloured line made into a short string a letter a character, for a test (c command, m missing, f flag, s string, o open string, v variable, n number, p operator, # comment, u path, d folder, k keyword, . none).</summary>
    public string LineKindsText()
    {
        var text = new char[lineRow < 0 ? 0 : lineLength];
        for (int i = 0; i < text.Length; i++)
        {
            text[i] = lineKinds[i] switch
            {
                TerminalLineKind.Command => 'c', TerminalLineKind.MissingCommand => 'm', TerminalLineKind.Keyword => 'k',
                TerminalLineKind.Flag => 'f', TerminalLineKind.String => 's', TerminalLineKind.OpenString => 'o',
                TerminalLineKind.Variable => 'v', TerminalLineKind.Number => 'n', TerminalLineKind.Operator => 'p',
                TerminalLineKind.Comment => '#', TerminalLineKind.Path => 'u', TerminalLineKind.Folder => 'd', _ => '.'
            };
        }

        return new string(text);
    }

    /// <summary>The line coloured now rather than on the next tick, for a test.</summary>
    internal void ColourLine()
    {
        UpdateLine();
        UpdateCompletion();
    }

    /// <summary>The line read off the screen and coloured again when the screen has changed since, every tick.</summary>
    private void UpdateLine()
    {
        if (lineColourer is null && completer is null || screen.AlternateScreen)
        {
            lineRow = -1;
            return;
        }

        // the screen changed, or the colourer would say something else of the same line
        int colourer = lineColourer?.Version ?? 0;
        if (screen.Version == colouredVersion && colourer == colouredBy)
            return;
        colouredVersion = screen.Version;
        colouredBy = colourer;

        if (InputStart() is not { } start)
        {
            lineRow = -1;
            return;
        }

        // The typed line from the end of the prompt along its row and the rows it wrapped onto
        int stride = screen.Columns, length = 0;
        for (int row = start.Row; row < screen.TotalRows; row++)
        {
            TerminalRow cells = screen.RowAt(row);
            int first = row == start.Row ? start.Column : 0, end = Math.Min(cells.Length, stride);
            if (lineText.Length < length + stride)
            {
                Array.Resize(ref lineText, (length + stride) * 2);
                Array.Resize(ref lineKinds, lineText.Length);
            }

            // a row it didn't fill is padded so every row starts a stride further on
            for (int column = row == start.Row ? first : 0; column < stride; column++)
            {
                char c = ' ';
                if (column < end)
                {
                    ref TerminalCell cell = ref cells.Cells[column];
                    c = cell.IsContinuation ? 'W' : cell.CodePoint is > 0 and < 0x10000 ? (char)cell.CodePoint : cell.CodePoint == 0 ? ' ' : 'W';
                }

                lineText[length++] = c;
            }

            if (!cells.Wrapped)
                break;
        }

        while (length > 0 && lineText[length - 1] == ' ')
            length--;

        // PSReadLine's prediction, dim text after the cursor that isn't typed yet, isn't the line
        int cursor = (screen.Scrollback.Count + screen.CursorY - start.Row) * stride + screen.CursorX - start.Column;
        if (cursor >= 0 && cursor < length && OnlyDimFrom(start, stride, cursor, length))
            length = cursor;

        lineRow = start.Row;
        lineColumn = start.Column;
        lineStride = stride;
        lineLength = length;
        if (lineColourer is not null)
            lineColourer.Colour(lineText.AsSpan(0, length), lineKinds.AsSpan(0, length), WorkingDirectory);
        else
            lineKinds.AsSpan(0, length).Clear();
    }

    private bool OnlyDimFrom((int Row, int Column) start, int stride, int from, int to)
    {
        for (int at = from; at < to; at++)
        {
            int place = at + start.Column;
            TerminalRow row = screen.RowAt(start.Row + place / stride);
            int column = place % stride;
            if (column >= row.Length)
                continue;
            ref TerminalCell cell = ref row.Cells[column];
            if (cell.CodePoint > ' ' && !cell.Style.Has(TerminalAttributes.Dim) && !cell.Style.Has(TerminalAttributes.Italic))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Where the typed line starts, the shell's mark, or where cmd's own prompt ends (a drive, a path and a >) for a cmd
    /// that says nothing, null while the shell isn't at a prompt.
    /// </summary>
    private (int Row, int Column)? InputStart()
    {
        if (screen.Input is { } input)
            return input;
        if (screen.MarksSeen)
            return null;

        // cmd's prompt, C:\some\folder>
        int row = screen.Scrollback.Count + screen.CursorY;
        TerminalRow cells = screen.RowAt(row);
        int length = Math.Min(cells.Length, screen.Columns);
        if (length < 3 || !char.IsAsciiLetter((char)cells.Cells[0].CodePoint) || cells.Cells[1].CodePoint != ':' || cells.Cells[2].CodePoint != '\\')
            return null;
        for (int column = 3; column < length; column++)
        {
            if (cells.Cells[column].CodePoint == '>')
                return column + 1 <= screen.CursorX ? (row, column + 1) : null;
        }

        return null;
    }

    /// <summary>What the line's colour makes of a cell, None for one outside of it.</summary>
    private TerminalLineKind LineKindOf(int rowIndex, int column)
    {
        if (lineRow < 0 || rowIndex < lineRow || column >= lineStride)
            return TerminalLineKind.None;
        int at = (rowIndex - lineRow) * lineStride + column - lineColumn;
        return at >= 0 && at < lineLength ? lineKinds[at] : TerminalLineKind.None;
    }

    /// <summary>The ink of a run of the line, the kind's colour, or what the shell said where the kind only goes over plain cells.</summary>
    private static Vector4 LineInk(TerminalLineKind kind, in TerminalStyle style, Vector4 ink, TerminalPalette palette)
    {
        bool plain = style.Foreground.IsDefault && !style.Has(TerminalAttributes.Inverse);
        return kind switch
        {
            TerminalLineKind.Command or TerminalLineKind.Keyword => palette.Ansi[2],
            TerminalLineKind.MissingCommand => palette.Ansi[1],
            _ when !plain => ink,
            TerminalLineKind.Flag => palette.Ansi[6],
            TerminalLineKind.String => palette.Ansi[3],
            TerminalLineKind.OpenString => Vector4.Lerp(palette.Ansi[3], palette.Background, 0.45f),
            TerminalLineKind.Variable => palette.Ansi[5],
            TerminalLineKind.Number => palette.Ansi[4],
            TerminalLineKind.Operator => palette.Ansi[5],
            TerminalLineKind.Comment => Vector4.Lerp(palette.Foreground, palette.Background, 0.45f),
            TerminalLineKind.Folder => palette.Ansi[4],
            _ => ink
        };
    }
}
