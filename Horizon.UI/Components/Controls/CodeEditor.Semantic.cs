using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// Semantic highlighting, what a name is (a class, a field, a parameter) laid over the grammar's colours, from a language
// server's semantic tokens or whatever else knows the code. They're worked out for the text as it was a moment ago, so
// each line keeps the text it had when they were given, and a line typed into since goes back to the grammar's colours
// until new ones come, the same way the inlay hints do. Lines put in or taken out above are found where they moved to
// the way the grammar's own colours are (ColoursOf), by how many lines the file grew or shrank, no moving of anything.
public partial class CodeEditor
{
    // Flat CSR index of semantic spans per line (semanticSpans[semanticOffsets[line]..semanticOffsets[line + 1]])
    // and the line text captured when they were given, avoiding per-line List<SyntaxSpan> allocations.
    // Backing arrays are reused across LSP semantic token refreshes whenever Length fits to avoid LOH allocations.
    private SyntaxSpan[] semanticSpans = [];
    private int[] semanticOffsets = [];
    private string?[] semanticTexts = [];
    private int semanticLineCount;
    private int semanticVersion;
    private IReadOnlyList<SemanticSpan> semanticList = [];

    // Every line's grammar colours with its names laid over, made once and kept as long as neither changes. Its list is
    // used again when one of them does, so drawing allocates nothing once a line has been seen
    private readonly Dictionary<int, (List<SyntaxSpan> Under, int OverVersion, int OverStart, int OverLength, List<SyntaxSpan> Laid)> overlaid = [];
    private const int MAX_OVERLAID = 4096;

    /// <summary>
    /// What the names in the code are (see <see cref="SemanticSpan"/>), for the text as it is now, laid over the grammar's
    /// colours as they're drawn. Empty for none, everything the grammar's colour.
    /// </summary>
    public IReadOnlyList<SemanticSpan> SemanticSpans
    {
        get => semanticList;
        set
        {
            semanticList = value ?? [];
            overlaid.Clear();
            semanticVersion++;

            int count = document.LineCount;
            int total = semanticList.Count;
            if (count <= 0 || total == 0)
            {
                if (semanticLineCount > 0)
                    Array.Clear(semanticTexts, 0, Math.Min(semanticLineCount, semanticTexts.Length));
                semanticLineCount = 0;
                return;
            }

            // Pass 1: count valid spans per line, reusing semanticOffsets when it fits.
            int neededOffsets = count + 1;
            int[] offsets = semanticOffsets;
            if (offsets.Length < neededOffsets)
                offsets = semanticOffsets = new int[neededOffsets];
            else
                Array.Clear(offsets, 0, neededOffsets);

            int valid = 0;
            for (int i = 0; i < total; i++)
            {
                SemanticSpan span = semanticList[i];
                if ((uint)span.Line < (uint)count && span.Length > 0)
                {
                    offsets[span.Line + 1]++;
                    valid++;
                }
            }

            if (valid == 0)
            {
                if (semanticLineCount > 0)
                    Array.Clear(semanticTexts, 0, Math.Min(semanticLineCount, semanticTexts.Length));
                semanticLineCount = 0;
                return;
            }

            // Prefix sum: offsets[line] = start index for line, offsets[line + 1] = end index
            for (int i = 1; i <= count; i++)
                offsets[i] += offsets[i - 1];

            // Pass 2: scatter into flat CSR array using offsets[line] as write cursor, reusing backing arrays when they fit.
            SyntaxSpan[] flat = semanticSpans;
            if (flat.Length < valid)
                flat = semanticSpans = new SyntaxSpan[valid];

            string?[] texts = semanticTexts;
            if (texts.Length < count)
                texts = semanticTexts = new string?[count];
            else
                Array.Clear(texts, 0, Math.Min(Math.Max(count, semanticLineCount), texts.Length));

            for (int i = 0; i < total; i++)
            {
                SemanticSpan span = semanticList[i];
                int line = span.Line;
                if ((uint)line >= (uint)count || span.Length <= 0)
                    continue;

                texts[line] ??= document.LineAt(line);
                flat[offsets[line]++] = new SyntaxSpan(span.Start, span.Length, span.Kind, span.Modifiers);
            }

            // Restore row start offsets after in-place scatter (offsets[line] currently holds the row's end)
            for (int i = count; i > 0; i--)
                offsets[i] = offsets[i - 1];
            offsets[0] = 0;

            // Sort only lines whose spans arrived out of Start order (most LSP tokens are already sorted)
            for (int line = 0; line < count; line++)
            {
                int start = offsets[line], end = offsets[line + 1];
                if (end - start <= 1)
                    continue;

                bool sorted = true;
                for (int k = start + 1; k < end; k++)
                {
                    if (flat[k].Start < flat[k - 1].Start)
                    {
                        sorted = false;
                        break;
                    }
                }

                if (!sorted)
                    flat.AsSpan(start, end - start).Sort(static (a, b) => a.Start.CompareTo(b.Start));
            }

            semanticLineCount = count;
        }
    }

    /// <summary>The names of a line, empty when it has none or its text isn't what it was when they were given.</summary>
    private bool TryGetSemanticsOf(int line, string text, out int start, out int length)
    {
        int count = semanticLineCount;
        if (count == 0)
        {
            start = length = 0;
            return false;
        }

        if ((uint)line < (uint)count)
        {
            int s = semanticOffsets[line], e = semanticOffsets[line + 1];
            if (e > s && string.Equals(semanticTexts[line], text, StringComparison.Ordinal))
            {
                start = s;
                length = e - s;
                return true;
            }
        }

        int was = line - (document.LineCount - count);
        if ((uint)was < (uint)count)
        {
            int s = semanticOffsets[was], e = semanticOffsets[was + 1];
            if (e > s && string.Equals(semanticTexts[was], text, StringComparison.Ordinal))
            {
                start = s;
                length = e - s;
                return true;
            }
        }

        start = length = 0;
        return false;
    }

    /// <summary>The names of a line as a span, empty when it has none or its text isn't what it was when they were given.</summary>
    internal ReadOnlySpan<SyntaxSpan> SemanticsOf(int line, string text) =>
        TryGetSemanticsOf(line, text, out int start, out int length) ? semanticSpans.AsSpan(start, length) : ReadOnlySpan<SyntaxSpan>.Empty;

    /// <summary>
    /// The colours a line is drawn in, the grammar's (as the last colouring had them, or worked out for the line alone
    /// while it's typed into) with what its names are laid over them. Null while it can't be coloured at all.
    /// </summary>
    private List<SyntaxSpan>? DrawnColours(int line, string text)
    {
        List<SyntaxSpan>? under = ColoursOf(line, text) ?? FreshColours(line, text);
        if (under is null || !Highlight || !TryGetSemanticsOf(line, text, out int overStart, out int overLength))
            return under;

        if (overlaid.TryGetValue(line, out var kept)
            && ReferenceEquals(kept.Under, under)
            && kept.OverVersion == semanticVersion
            && kept.OverStart == overStart
            && kept.OverLength == overLength)
            return kept.Laid;

        if (kept.Laid is null && overlaid.Count >= MAX_OVERLAID)
            overlaid.Clear();

        List<SyntaxSpan> laid = kept.Laid ?? new List<SyntaxSpan>(under.Count + overLength);
        SemanticOverlay.Overlay(under, semanticSpans.AsSpan(overStart, overLength), laid);
        overlaid[line] = (under, semanticVersion, overStart, overLength, laid);
        return laid;
    }

    /// <summary>What the character at a place is drawn as, the kind and what else it is, a plain name for one in no span.</summary>
    public (SyntaxKind Kind, SyntaxModifiers Modifiers) DrawnAt(int line, int column)
    {
        if (line < 0 || line >= document.LineCount)
            return (SyntaxKind.Name, SyntaxModifiers.None);

        string text = document.LineAt(line);
        foreach (SyntaxSpan span in DrawnColours(line, text) ?? [])
        {
            if (column >= span.Start && column < span.Start + span.Length)
                return (span.Kind, span.Modifiers);
        }

        return (SyntaxKind.Name, SyntaxModifiers.None);
    }

    /// <summary>
    /// What else a span is drawn with, ReSharper's way. Something obsolete struck through the middle of its small letters
    /// (see StrikeY), a local that's written to again underlined, both a thin line in the colour of its text a little faded.
    /// </summary>
    private static void DrawModifiers(UIDrawList list, SyntaxModifiers modifiers, float from, float to, float top, float lineHeight, float strikeY, Vector4 colour)
    {
        if ((modifiers & (SyntaxModifiers.Deprecated | SyntaxModifiers.Mutable)) == 0 || to <= from)
            return;

        Vector4 faded = colour with { W = colour.W * 0.8f };
        if ((modifiers & SyntaxModifiers.Deprecated) != 0)
            Strike(list, from, to, strikeY, faded);

        if ((modifiers & SyntaxModifiers.Mutable) != 0)
        {
            // y goes up the screen, the bottom of the row is a whole line below its top
            float y = MathF.Round(top - lineHeight * 0.88f);
            list.Rect(new UIRect(new Vector2(from, y), new Vector2(to, y + 1.0f)), faded);
        }
    }

    /// <summary>
    /// The sixteen colours a terminal draws in, for the Ansi kinds of what the editor's given to show (what a notebook's
    /// code printed, coloured by its escapes). Null, the usual, draws them as plain text.
    /// </summary>
    public Vector4[]? AnsiColours { get; set; }

    /// <summary>A span's colour, by its kind and what else it is, in a set of colours.</summary>
    private Vector4 ColourOf(UISyntaxColors colors, in SyntaxSpan span)
    {
        if (span.Kind >= SyntaxKind.Ansi0)
            return AnsiColours is { Length: 16 } ansi ? ansi[span.Kind - SyntaxKind.Ansi0] : colors.Of(SyntaxKind.Name);
        return span.Modifiers == SyntaxModifiers.None ? colors.Of(span.Kind) : colors.Of(span.Kind, span.Modifiers);
    }
}
