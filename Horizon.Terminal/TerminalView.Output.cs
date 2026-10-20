using System.Numerics;

namespace Horizon.UI.Components;

// What programs write coloured by rules where they left it plain (TerminalOutputHighlighter), the rows on screen
// only, each read again only when it changed (its cells hash to something else, or another row is in its place). The
// codes go in arrays kept and grown, the paint reads them, nothing is made in a frame.
public partial class TerminalView
{
    /// <summary>What colours the output by what it looks like, null to leave it as the programs wrote it.</summary>
    public TerminalOutputHighlighter? OutputHighlighter
    {
        get => outputHighlighter;
        set
        {
            outputHighlighter = value;
            Array.Clear(outputRows);
        }
    }

    private TerminalOutputHighlighter? outputHighlighter;

    // A visible row's codes, a stride of columns each, and what the row was when they were worked out
    private byte[] outputCodes = [];
    private TerminalRow?[] outputRows = [];
    private int[] outputHashes = [];
    private char[] outputText = new char[256];
    private int outputStride, outputOffset, outputVersion = -1, outputRulesVersion = -1;

    /// <summary>The rows on screen coloured again where they changed, every tick, nothing when there is no highlighter or a program has the whole screen.</summary>
    private void UpdateOutput()
    {
        if (outputHighlighter is null || screen.AlternateScreen)
        {
            outputStride = 0;
            return;
        }

        int offset = RowOffset(), rows = screen.Rows, stride = screen.Columns;
        if (screen.Version == outputVersion && offset == outputOffset && outputHighlighter.Version == outputRulesVersion && stride == outputStride)
            return;

        if (outputRows.Length < rows || stride != outputStride)
        {
            outputRows = new TerminalRow?[rows];
            outputHashes = new int[rows];
            outputCodes = new byte[rows * stride];
        }

        if (outputHighlighter.Version != outputRulesVersion)
            Array.Clear(outputRows);

        outputVersion = screen.Version;
        outputOffset = offset;
        outputStride = stride;
        outputRulesVersion = outputHighlighter.Version;
        if (outputText.Length < stride)
            outputText = new char[stride * 2];

        for (int y = 0; y < rows; y++)
        {
            int index = offset + y;
            Span<byte> codes = outputCodes.AsSpan(y * stride, stride);
            if (index >= screen.TotalRows)
            {
                outputRows[y] = null;
                codes.Clear();
                continue;
            }

            TerminalRow row = screen.RowAt(index);
            int length = Math.Min(row.Length, stride), hash = 17;
            for (int x = 0; x < length; x++)
                hash = hash * 31 + row.Cells[x].CodePoint;
            if (ReferenceEquals(outputRows[y], row) && outputHashes[y] == hash)
                continue;

            outputRows[y] = row;
            outputHashes[y] = hash;
            int used = row.Used;
            for (int x = 0; x < used && x < stride; x++)
            {
                ref TerminalCell cell = ref row.Cells[x];
                outputText[x] = cell.IsContinuation ? 'W' : cell.CodePoint is > 0 and < 0x10000 ? (char)cell.CodePoint : ' ';
            }

            codes.Clear();
            outputHighlighter.Colour(outputText.AsSpan(0, Math.Min(used, stride)), codes);
        }
    }

    /// <summary>What a rule made of a cell on screen, 0 for nothing or a row that isn't coloured.</summary>
    private byte OutputCodeOf(int rowIndex, int column)
    {
        if (outputStride == 0 || column >= outputStride)
            return 0;
        int y = rowIndex - outputOffset;
        return y >= 0 && y * outputStride < outputCodes.Length ? outputCodes[y * outputStride + column] : (byte)0;
    }

    /// <summary>What a rule matched written in its colour, where the program left the text plain.</summary>
    private static Vector4 OutputInk(byte code, in TerminalStyle style, Vector4 ink, TerminalPalette palette)
    {
        if (!style.Foreground.IsDefault || style.Has(TerminalAttributes.Inverse))
            return ink;
        return (TerminalOutputColour)(code & 0x0F) switch
        {
            TerminalOutputColour.Red => palette.Ansi[1],
            TerminalOutputColour.Green => palette.Ansi[2],
            TerminalOutputColour.Yellow => palette.Ansi[3],
            TerminalOutputColour.Blue => palette.Ansi[4],
            TerminalOutputColour.Magenta => palette.Ansi[5],
            TerminalOutputColour.Cyan => palette.Ansi[6],
            TerminalOutputColour.Dim => Vector4.Lerp(ink, palette.Background, 0.45f),
            _ => ink
        };
    }

    /// <summary>The kinds of a row on screen as letters (r g y b m c d, upper case underlined, . nothing), for a test.</summary>
    public string OutputCodesText(int screenRow)
    {
        var text = new char[outputStride];
        for (int x = 0; x < text.Length; x++)
        {
            byte code = OutputCodeOf(outputOffset + screenRow, x);
            char letter = (TerminalOutputColour)(code & 0x0F) switch
            {
                TerminalOutputColour.Red => 'r', TerminalOutputColour.Green => 'g', TerminalOutputColour.Yellow => 'y',
                TerminalOutputColour.Blue => 'b', TerminalOutputColour.Magenta => 'm', TerminalOutputColour.Cyan => 'c',
                TerminalOutputColour.Dim => 'd', _ => '.'
            };
            text[x] = (code & TerminalOutputRule.UNDERLINE) != 0 ? char.ToUpperInvariant(letter) : letter;
        }

        return new string(text).TrimEnd('.');
    }

    /// <summary>The output coloured now rather than on the next tick, for a test.</summary>
    internal void ColourOutput() => UpdateOutput();
}
