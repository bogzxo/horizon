using System.Numerics;

namespace Horizon.UI.Components;

/// <summary>
/// A colour as a terminal names one. The default (the theme's foreground or background), one of the 256 of the
/// palette (the first sixteen the theme's, the rest the cube and the greys), or a true colour.
/// </summary>
public readonly record struct TerminalColor(TerminalColorKind Kind, byte Index, byte R, byte G, byte B)
{
    public static readonly TerminalColor Default = new(TerminalColorKind.Default, 0, 0, 0, 0);

    public static TerminalColor Indexed(int index) => new(TerminalColorKind.Indexed, (byte)Math.Clamp(index, 0, 255), 0, 0, 0);

    public static TerminalColor Rgb(int r, int g, int b) => new(TerminalColorKind.Rgb, 0, (byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));

    public bool IsDefault => Kind == TerminalColorKind.Default;
}

public enum TerminalColorKind : byte { Default, Indexed, Rgb }

/// <summary>What is on in a cell's style, SGR's attributes.</summary>
[Flags]
public enum TerminalAttributes : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Invisible = 64,
    Strikethrough = 128,
    Overline = 256,

    // Not SGR's, the cell's own. A wide character's first cell and the one after it that it covers
    Wide = 1024,
    Continuation = 2048
}

/// <summary>The kinds of underline SGR 4:n gives, the curly one is what a spell checker and a language server draw.</summary>
public enum TerminalUnderline : byte { None, Single, Double, Curly, Dotted, Dashed }

/// <summary>
/// How a cell is drawn, the colours and the attributes SGR sets, and the hyperlink it is part of (an id into the
/// screen's links, 0 for none). The style the cursor writes with is one of these too.
/// </summary>
public struct TerminalStyle : IEquatable<TerminalStyle>
{
    public TerminalColor Foreground, Background, UnderlineColor;
    public TerminalAttributes Attributes;
    public TerminalUnderline Underline;
    public ushort Link;

    public static readonly TerminalStyle Plain = new() { Foreground = TerminalColor.Default, Background = TerminalColor.Default, UnderlineColor = TerminalColor.Default };

    public readonly bool Has(TerminalAttributes attribute) => (Attributes & attribute) != 0;

    /// <summary>The same look, the wide and continuation flags apart, which is what makes two cells one run on screen.</summary>
    public readonly bool LooksLike(in TerminalStyle other) =>
        Foreground == other.Foreground && Background == other.Background && UnderlineColor == other.UnderlineColor && Underline == other.Underline && Link == other.Link
        && (Attributes & ~(TerminalAttributes.Wide | TerminalAttributes.Continuation)) == (other.Attributes & ~(TerminalAttributes.Wide | TerminalAttributes.Continuation));

    public readonly bool Equals(TerminalStyle other) => LooksLike(other) && Attributes == other.Attributes;

    public override readonly bool Equals(object? obj) => obj is TerminalStyle other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Foreground, Background, UnderlineColor, Attributes, Underline, Link);

    public static bool operator ==(TerminalStyle left, TerminalStyle right) => left.Equals(right);

    public static bool operator !=(TerminalStyle left, TerminalStyle right) => !left.Equals(right);
}

/// <summary>
/// One cell of the screen. A code point (a space when nothing was written, 0 under a wide character), the marks that
/// combine onto it when there are any (rare, so a string and null the rest of the time), and its style.
/// </summary>
public struct TerminalCell
{
    public int CodePoint;
    public string? Combining;
    public TerminalStyle Style;

    public static readonly TerminalCell Blank = new() { CodePoint = ' ', Style = TerminalStyle.Plain };

    /// <summary>A blank cell in a style, what erasing leaves behind (the background stays, the rest goes).</summary>
    public static TerminalCell Erased(in TerminalStyle style) => new() { CodePoint = ' ', Style = new TerminalStyle { Foreground = TerminalColor.Default, Background = style.Background, UnderlineColor = TerminalColor.Default } };

    public readonly bool IsBlank => CodePoint == ' ' && Combining is null;

    public readonly bool IsContinuation => Style.Has(TerminalAttributes.Continuation);
}

/// <summary>A row of the screen or the scrollback, and whether it ran on into the row after it (soft wrapped) or ended with a line feed.</summary>
public sealed class TerminalRow
{
    public TerminalCell[] Cells;
    public bool Wrapped;

    /// <summary>What the shell integration marks said of the row, see <see cref="TerminalRowMark"/>.</summary>
    public TerminalRowMark Mark;

    // What a view's minimap made of the row, its runs of ink, kept until the row changes so a minimap of thousands of
    // rows goes through the cells of the few that did (TerminalView.Minimap)
    internal Horizon.UI.Drawing.UIMinimapBar[]? MinimapBars;
    internal int MinimapBarCount;
    internal long MinimapStamp = -1;

    public TerminalRow(int columns)
    {
        Cells = new TerminalCell[columns];
        Array.Fill(Cells, TerminalCell.Blank);
    }

    public TerminalRow(TerminalCell[] cells, bool wrapped)
    {
        Cells = cells;
        Wrapped = wrapped;
    }

    public int Length => Cells.Length;

    /// <summary>The cells to the last one that isn't blank, what the text of the row is.</summary>
    public int Used
    {
        get
        {
            int used = Cells.Length;
            while (used > 0 && Cells[used - 1].IsBlank && Cells[used - 1].Style.Background.IsDefault)
                used--;
            return used;
        }
    }

    public void Fill(in TerminalCell cell, int from, int to)
    {
        for (int i = Math.Max(0, from); i < Math.Min(to, Cells.Length); i++)
            Cells[i] = cell;
    }

    /// <summary>The row as text, for copying and tests, a wide character once and nothing for the cell after it.</summary>
    public string Text(int from = 0, int to = int.MaxValue)
    {
        var text = new System.Text.StringBuilder();
        int last = Math.Min(to, Cells.Length);
        while (last > 0 && Cells[last - 1].IsBlank)
            last--;
        to = last;
        for (int i = Math.Max(0, from); i < to; i++)
        {
            if (Cells[i].IsContinuation)
                continue;
            text.Append(char.ConvertFromUtf32(Cells[i].CodePoint));
            if (Cells[i].Combining is { } marks)
                text.Append(marks);
        }

        return text.ToString();
    }
}

/// <summary>The sixteen colours of a terminal, as a skin or a theme gives them, and the rest of the 256 worked out.</summary>
public sealed class TerminalPalette
{
    public Vector4 Foreground = new(0.84f, 0.87f, 0.93f, 1.0f);
    public Vector4 Background = new(0.055f, 0.06f, 0.08f, 1.0f);
    public Vector4 Cursor = new(0.84f, 0.87f, 0.93f, 1.0f);
    public Vector4 Selection = new(0.38f, 0.62f, 1.0f, 0.35f);

    /// <summary>The sixteen ANSI colours, black to white and their bright halves, xterm's by default.</summary>
    public readonly Vector4[] Ansi =
    [
        new(0.0f, 0.0f, 0.0f, 1.0f), new(0.8f, 0.0f, 0.0f, 1.0f), new(0.0f, 0.8f, 0.0f, 1.0f), new(0.8f, 0.8f, 0.0f, 1.0f),
        new(0.0f, 0.0f, 0.93f, 1.0f), new(0.8f, 0.0f, 0.8f, 1.0f), new(0.0f, 0.8f, 0.8f, 1.0f), new(0.9f, 0.9f, 0.9f, 1.0f),
        new(0.5f, 0.5f, 0.5f, 1.0f), new(1.0f, 0.0f, 0.0f, 1.0f), new(0.0f, 1.0f, 0.0f, 1.0f), new(1.0f, 1.0f, 0.0f, 1.0f),
        new(0.36f, 0.36f, 1.0f, 1.0f), new(1.0f, 0.0f, 1.0f, 1.0f), new(0.0f, 1.0f, 1.0f, 1.0f), new(1.0f, 1.0f, 1.0f, 1.0f)
    ];

    /// <summary>
    /// The colours a skin's theme gives a terminal. The panel is the background and the text the foreground, the
    /// accent the cursor and (faded) the selection, and the sixteen are worked out of the colours the palette has
    /// names for, the errors red, the successes green, the warnings yellow, the links blue, the keywords magenta and
    /// the types cyan, the bright eight a step towards the text. A light theme comes out with dark ink on a light
    /// ground, the way its panel reads, see <see cref="Skinning.UIPalette.IsDark"/>.
    /// </summary>
    public static TerminalPalette FromPalette(Skinning.UIPalette palette)
    {
        static Vector4 Opaque(Vector4 colour) => colour with { W = 1.0f };
        Vector4 ground = Opaque(palette.Panel), ink = Opaque(palette.Text);
        bool dark = palette.IsDark;
        Vector4 Bright(Vector4 colour) => Skinning.UIPalette.Towards(colour, ink, 0.3f);

        var made = new TerminalPalette
        {
            Background = ground,
            Foreground = ink,
            Cursor = Opaque(palette.Highlight),
            Selection = palette.Selection
        };

        Vector4 black = dark ? Skinning.UIPalette.Towards(ground, ink, 0.1f) : Skinning.UIPalette.Towards(ink, ground, 0.1f);
        Vector4 white = dark ? Skinning.UIPalette.Towards(ink, ground, 0.18f) : Skinning.UIPalette.Towards(ground, ink, 0.3f);
        Vector4[] colours =
        [
            black, Opaque(palette.Error), Opaque(palette.Success), Opaque(palette.Warning),
            Opaque(palette.Link), Opaque(palette.Syntax.Keyword), Opaque(palette.Syntax.Type), white
        ];
        for (int i = 0; i < 8; i++)
        {
            made.Ansi[i] = colours[i];
            made.Ansi[i + 8] = i == 0 ? Opaque(palette.DimText) : i == 7 ? ink : Bright(colours[i]);
        }

        return made;
    }

    /// <summary>A terminal colour as one of the screen's, the 6x6x6 cube and the greys worked out the way xterm does.</summary>
    public Vector4 Of(in TerminalColor color, bool foreground)
    {
        switch (color.Kind)
        {
            case TerminalColorKind.Rgb:
                return new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, 1.0f);

            case TerminalColorKind.Indexed when color.Index < 16:
                return Ansi[color.Index];

            case TerminalColorKind.Indexed when color.Index < 232:
            {
                int index = color.Index - 16;
                int r = index / 36, g = index / 6 % 6, b = index % 6;
                return new Vector4(Level(r), Level(g), Level(b), 1.0f);
            }

            case TerminalColorKind.Indexed:
            {
                float grey = (8 + (color.Index - 232) * 10) / 255.0f;
                return new Vector4(grey, grey, grey, 1.0f);
            }

            default:
                return foreground ? Foreground : Background;
        }
    }

    private static float Level(int step) => step == 0 ? 0.0f : (55 + step * 40) / 255.0f;
}
