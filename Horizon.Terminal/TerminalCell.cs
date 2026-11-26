using System.Numerics;
using System.Runtime.CompilerServices;

namespace Horizon.UI.Components;

/// <summary>
/// A colour as a terminal names one. The default (the theme's foreground or background), one of the 256 of the
/// palette (the first sixteen the theme's, the rest the cube and the greys), or a true colour.
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    private readonly uint packed;

    public static readonly TerminalColor Default = default;

    private TerminalColor(uint packed) => this.packed = packed;

    public TerminalColor(TerminalColorKind kind, byte index, byte r, byte g, byte b)
    {
        packed = kind switch
        {
            TerminalColorKind.Indexed => (uint)TerminalColorKind.Indexed | ((uint)index << 8),
            TerminalColorKind.Rgb => (uint)TerminalColorKind.Rgb | ((uint)r << 8) | ((uint)g << 16) | ((uint)b << 24),
            _ => 0u
        };
    }

    public TerminalColorKind Kind => (TerminalColorKind)(packed & 0xFF);

    public byte Index => Kind == TerminalColorKind.Indexed ? (byte)(packed >> 8) : (byte)0;

    public byte R => Kind == TerminalColorKind.Rgb ? (byte)(packed >> 8) : (byte)0;

    public byte G => Kind == TerminalColorKind.Rgb ? (byte)(packed >> 16) : (byte)0;

    public byte B => Kind == TerminalColorKind.Rgb ? (byte)(packed >> 24) : (byte)0;

    public static TerminalColor Indexed(int index) => new((uint)TerminalColorKind.Indexed | ((uint)Math.Clamp(index, 0, 255) << 8));

    public static TerminalColor Rgb(int r, int g, int b) => new((uint)TerminalColorKind.Rgb | ((uint)Math.Clamp(r, 0, 255) << 8) | ((uint)Math.Clamp(g, 0, 255) << 16) | ((uint)Math.Clamp(b, 0, 255) << 24));

    public bool IsDefault => packed == 0;

    public bool Equals(TerminalColor other) => packed == other.packed;

    public override bool Equals(object? obj) => obj is TerminalColor other && packed == other.packed;

    public override int GetHashCode() => (int)packed;

    public static bool operator ==(TerminalColor left, TerminalColor right) => left.packed == right.packed;

    public static bool operator !=(TerminalColor left, TerminalColor right) => left.packed != right.packed;

    public override string ToString() => Kind switch
    {
        TerminalColorKind.Indexed => $"Indexed({Index})",
        TerminalColorKind.Rgb => $"Rgb({R}, {G}, {B})",
        _ => "Default"
    };
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

    // Cell-local metadata flags (not SGR): combining marks live sparsely on TerminalRow; wide/continuation mark double-width glyphs
    HasCombining = 512,
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
    private const ushort ATTRIBUTES_MASK = 0x0FFF;
    private const ushort UNDERLINE_MASK = 0xF000;
    private const ushort LAYOUT_MASK = (ushort)(TerminalAttributes.HasCombining | TerminalAttributes.Wide | TerminalAttributes.Continuation);

    public TerminalColor Foreground, Background, UnderlineColor;
    private ushort attrsAndUnderline;
    public ushort Link;

    public TerminalAttributes Attributes
    {
        readonly get => (TerminalAttributes)(attrsAndUnderline & ATTRIBUTES_MASK);
        set => attrsAndUnderline = (ushort)((attrsAndUnderline & UNDERLINE_MASK) | ((ushort)value & ATTRIBUTES_MASK));
    }

    public TerminalUnderline Underline
    {
        readonly get => (TerminalUnderline)(attrsAndUnderline >> 12);
        set => attrsAndUnderline = (ushort)((attrsAndUnderline & ATTRIBUTES_MASK) | (((ushort)value & 0x0F) << 12));
    }

    public static readonly TerminalStyle Plain = default;

    public readonly bool Has(TerminalAttributes attribute) => (attrsAndUnderline & (ushort)attribute) != 0;

    /// <summary>The same look, the wide and continuation flags apart, which is what makes two cells one run on screen.</summary>
    public readonly bool LooksLike(in TerminalStyle other) =>
        Foreground == other.Foreground && Background == other.Background && UnderlineColor == other.UnderlineColor && Link == other.Link
        && (attrsAndUnderline & ~LAYOUT_MASK) == (other.attrsAndUnderline & ~LAYOUT_MASK);

    public readonly bool Equals(TerminalStyle other) =>
        Foreground == other.Foreground && Background == other.Background && UnderlineColor == other.UnderlineColor
        && attrsAndUnderline == other.attrsAndUnderline && Link == other.Link;

    public override readonly bool Equals(object? obj) => obj is TerminalStyle other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Foreground, Background, UnderlineColor, attrsAndUnderline, Link);

    public static bool operator ==(TerminalStyle left, TerminalStyle right) => left.Equals(right);

    public static bool operator !=(TerminalStyle left, TerminalStyle right) => !left.Equals(right);
}

/// <summary>
/// One cell of the screen as an unmanaged blittable struct (zero managed references, zero GC write barriers on cell
/// writes/fills/copies and scrollback arrays). Rare combining marks live sparsely on <see cref="TerminalRow"/> gated
/// by <see cref="TerminalAttributes.HasCombining"/>.
/// </summary>
public struct TerminalCell
{
    public int CodePoint;
    public TerminalStyle Style;
    internal ushort CombiningToken;

    public static readonly TerminalCell Blank = new() { CodePoint = ' ', Style = TerminalStyle.Plain };

    /// <summary>A blank cell in a style, what erasing leaves behind (the background stays, the rest goes).</summary>
    public static TerminalCell Erased(in TerminalStyle style) => new() { CodePoint = ' ', Style = new TerminalStyle { Foreground = TerminalColor.Default, Background = style.Background, UnderlineColor = TerminalColor.Default } };

    public readonly bool HasCombining => Style.Has(TerminalAttributes.HasCombining);

    /// <summary>Combining marks attached to this cell, or null when none (resolved without holding a managed reference in the struct).</summary>
    public readonly string? Combining => HasCombining ? TerminalRow.ResolveCombiningToken(CombiningToken) : null;

    public readonly bool IsBlank => CodePoint == ' ' && !HasCombining;

    public readonly bool IsPlainBlank => CodePoint == ' ' && !HasCombining && Style == TerminalStyle.Plain;

    public readonly bool IsContinuation => Style.Has(TerminalAttributes.Continuation);
}

/// <summary>A row of the screen or the scrollback, and whether it ran on into the row after it (soft wrapped) or ended with a line feed.</summary>
public sealed class TerminalRow
{
    // Bounded intern table for rare combining-mark sequences so TerminalCell.Combining remains readable while keeping
    // TerminalCell 100% unmanaged; TerminalRow's sparse dictionary is the per-row authoritative store.
    private const int MAX_COMBINING_TOKENS = 4096;
    private static readonly Lock CombiningLock = new();
    private static readonly Dictionary<string, ushort> CombiningToToken = new(StringComparer.Ordinal);
    private static readonly List<string> TokenToCombining = [string.Empty];

    internal static ushort InternCombining(string marks)
    {
        if (string.IsNullOrEmpty(marks))
            return 0;

        lock (CombiningLock)
        {
            if (CombiningToToken.TryGetValue(marks, out ushort existing))
                return existing;
            if (TokenToCombining.Count >= MAX_COMBINING_TOKENS)
                return 0;
            ushort id = (ushort)TokenToCombining.Count;
            TokenToCombining.Add(marks);
            CombiningToToken[marks] = id;
            return id;
        }
    }

    internal static string? ResolveCombiningToken(ushort token)
    {
        if (token == 0)
            return null;
        lock (CombiningLock)
            return token < TokenToCombining.Count ? TokenToCombining[token] : null;
    }

    public TerminalCell[] Cells;
    public bool Wrapped;

    /// <summary>Whether any cell in this row may carry a non-default background or Inverse attribute (lets PaintRow skip Pass 1 when false).</summary>
    public bool HasStyledBackground;

    // Sparse combining marks keyed by column index; null for 99.9%+ of rows.
    private Dictionary<int, string>? combining;

    /// <summary>What the shell integration marks said of the row, see <see cref="TerminalRowMark"/>.</summary>
    public TerminalRowMark Mark;

    /// <summary>Goes up whenever this row's cells change, so minimap, highlighters and partial redraws skip untouched rows.</summary>
    public int Generation { get; private set; }

    internal void Touch() => Generation++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool StyleHasBackground(in TerminalStyle style) =>
        !style.Background.IsDefault || style.Has(TerminalAttributes.Inverse);

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
        for (int i = 0; i < cells.Length; i++)
        {
            if (StyleHasBackground(cells[i].Style))
            {
                HasStyledBackground = true;
                break;
            }
        }
    }

    internal TerminalRow(TerminalCell[] cells, bool wrapped, bool hasStyledBackground)
    {
        Cells = cells;
        Wrapped = wrapped;
        HasStyledBackground = hasStyledBackground;
    }

    public int Length => Cells.Length;

    /// <summary>Combining marks at a column, or null when the cell has none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? CombiningAt(int column) =>
        combining is not null && (uint)column < (uint)Cells.Length && Cells[column].HasCombining && combining.TryGetValue(column, out string? marks)
            ? marks
            : null;

    /// <summary>Appends a combining mark to the cell at <paramref name="column"/>.</summary>
    internal void AppendCombining(int column, string mark)
    {
        if ((uint)column >= (uint)Cells.Length || string.IsNullOrEmpty(mark))
            return;

        combining ??= [];
        string updated = combining.TryGetValue(column, out string? existing) ? existing + mark : mark;
        combining[column] = updated;
        ref TerminalCell cell = ref Cells[column];
        cell.Style.Attributes |= TerminalAttributes.HasCombining;
        cell.CombiningToken = InternCombining(updated);
        Generation++;
    }

    /// <summary>Sets or clears the combining marks at <paramref name="column"/>.</summary>
    internal void SetCombining(int column, string? marks)
    {
        if ((uint)column >= (uint)Cells.Length)
            return;

        ref TerminalCell cell = ref Cells[column];
        if (string.IsNullOrEmpty(marks))
        {
            cell.Style.Attributes &= ~TerminalAttributes.HasCombining;
            cell.CombiningToken = 0;
            if (combining is not null && combining.Remove(column) && combining.Count == 0)
                combining = null;
            return;
        }

        combining ??= [];
        combining[column] = marks;
        cell.Style.Attributes |= TerminalAttributes.HasCombining;
        cell.CombiningToken = InternCombining(marks);
    }

    /// <summary>Clears any sparse combining entries in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    internal void ClearCombining(int from, int to)
    {
        if (combining is null)
            return;

        for (int i = from; i < to; i++)
            combining.Remove(i);
        if (combining.Count == 0)
            combining = null;
    }

    /// <summary>Drops all sparse combining entries on this row.</summary>
    internal void ClearCombiningAll() => combining = null;

    /// <summary>Shifts sparse combining entries right by <paramref name="count"/> columns starting at <paramref name="from"/>.</summary>
    internal void ShiftCombiningRight(int from, int count, int columns)
    {
        if (combining is null || count <= 0)
            return;

        for (int c = columns - 1; c >= from + count; c--)
        {
            if (combining.Remove(c - count, out string? marks))
                combining[c] = marks;
            else
                combining.Remove(c);
        }
        for (int c = from; c < from + count && c < columns; c++)
            combining.Remove(c);
        if (combining.Count == 0)
            combining = null;
    }

    /// <summary>Shifts sparse combining entries left by <paramref name="count"/> columns starting at <paramref name="from"/>.</summary>
    internal void ShiftCombiningLeft(int from, int count, int columns)
    {
        if (combining is null || count <= 0)
            return;

        for (int c = from; c < columns - count; c++)
        {
            if (combining.Remove(c + count, out string? marks))
                combining[c] = marks;
            else
                combining.Remove(c);
        }
        for (int c = Math.Max(from, columns - count); c < columns; c++)
            combining.Remove(c);
        if (combining.Count == 0)
            combining = null;
    }

    /// <summary>The cell at a column, or a plain blank when the row was compacted shorter than the screen.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TerminalCell CellOrBlank(int column) => (uint)column < (uint)Cells.Length ? Cells[column] : TerminalCell.Blank;

    /// <summary>
    /// Drops trailing plain blank cells once the row enters the scrollback, returning the replaced full-width array so
    /// the screen can reuse it for a new grid row. Wrapped rows keep their length so reflow preserves trailing spaces.
    /// </summary>
    internal TerminalCell[]? CompactForScrollback()
    {
        if (Wrapped || Cells.Length == 0)
            return null;

        int used = Cells.Length;
        while (used > 0 && Cells[used - 1].IsPlainBlank)
            used--;

        if (used == Cells.Length)
            return null;

        TerminalCell[] previous = Cells;
        if (used == 0)
        {
            Cells = [];
            combining = null;
            HasStyledBackground = false;
        }
        else
        {
            var compact = new TerminalCell[used];
            Array.Copy(previous, compact, used);
            Cells = compact;
        }

        return previous;
    }

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
        int start = Math.Max(0, from);
        int end = Math.Min(to, Cells.Length);
        if (start >= end)
            return;
        for (int i = start; i < end; i++)
            Cells[i] = cell;
        if (combining is not null)
            ClearCombining(start, end);
        bool styled = StyleHasBackground(cell.Style);
        if (start == 0 && end == Cells.Length)
            HasStyledBackground = styled;
        else if (styled)
            HasStyledBackground = true;
        Generation++;
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
            if (Cells[i].HasCombining && CombiningAt(i) is { } marks)
                text.Append(marks);
        }

        return text.ToString();
    }
}

/// <summary>The sixteen colours of a terminal, as a skin or a theme gives them, and the rest of the 256 worked out.</summary>
public sealed class TerminalPalette
{
    // Precomputed 256-entry colour LUT (entries 16..255 are fixed xterm cube + greyscale ramp, shared across palettes).
    private static readonly Vector4[] Fixed256 = BuildFixed256();

    private static Vector4[] BuildFixed256()
    {
        var table = new Vector4[256];
        for (int i = 16; i < 232; i++)
        {
            int index = i - 16;
            int r = index / 36, g = index / 6 % 6, b = index % 6;
            table[i] = new Vector4(Level(r), Level(g), Level(b), 1.0f);
        }
        for (int i = 232; i < 256; i++)
        {
            float grey = (8 + (i - 232) * 10) / 255.0f;
            table[i] = new Vector4(grey, grey, grey, 1.0f);
        }
        return table;
    }

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

    // Per-palette 256-entry LUT (0..15 mirrored from Ansi, 16..255 from Fixed256)
    private readonly Vector4[] lut = new Vector4[256];

    public TerminalPalette() => RebuildLut();

    /// <summary>Synchronizes the 256-colour LUT with <see cref="Ansi"/>.</summary>
    public void RebuildLut()
    {
        Array.Copy(Ansi, 0, lut, 0, 16);
        Array.Copy(Fixed256, 16, lut, 16, 240);
    }

    /// <summary>Precomputed 256-colour lookup table.</summary>
    public ReadOnlySpan<Vector4> Lut => lut;

    /// <summary>
    /// The colours a skin's theme gives a terminal. The panel is the background and the text the foreground, the
    /// accent the cursor and (faded) the selection, and the sixteen are worked out of the colours the palette has
    /// names for, the errors red, the successes green, the warnings yellow, the links blue, the keywords magenta and
    /// the types cyan, the bright eight a step towards the text, and any of them the theme writes out under terminal is
    /// taken as written. A light theme comes out with dark ink on a light
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

        // The ones the theme wrote out itself go over what was worked out (Visual Studio's has Campbell's)
        if (palette.Terminal is { } given)
        {
            for (int i = 0; i < made.Ansi.Length && i < given.Length; i++)
            {
                if (given[i] is { } own)
                    made.Ansi[i] = Opaque(own);
            }
        }

        made.RebuildLut();
        return made;
    }

    /// <summary>A terminal colour as one of the screen's, O(1) via precomputed 256-entry LUT for indexed colours.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector4 Of(in TerminalColor color, bool foreground) => color.Kind switch
    {
        TerminalColorKind.Indexed => color.Index < 16 ? Ansi[color.Index] : lut[color.Index],
        TerminalColorKind.Rgb => new Vector4(color.R / 255.0f, color.G / 255.0f, color.B / 255.0f, 1.0f),
        _ => foreground ? Foreground : Background
    };

    private static float Level(int step) => step == 0 ? 0.0f : (55 + step * 40) / 255.0f;
}
