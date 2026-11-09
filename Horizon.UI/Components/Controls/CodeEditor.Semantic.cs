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
    // The names of every line as they were given, and the text each line had then
    private List<SyntaxSpan>?[] semanticLines = [];
    private string?[] semanticTexts = [];
    private IReadOnlyList<SemanticSpan> semanticList = [];

    // Every line's grammar colours with its names laid over, made once and kept as long as neither changes. Its list is
    // used again when one of them does, so drawing allocates nothing once a line has been seen
    private readonly Dictionary<int, (List<SyntaxSpan> Under, List<SyntaxSpan> Over, List<SyntaxSpan> Laid)> overlaid = [];
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

            int count = document.LineCount;
            var lines = new List<SyntaxSpan>?[count];
            var texts = new string?[count];
            foreach (SemanticSpan span in semanticList)
            {
                if (span.Line < 0 || span.Line >= count || span.Length <= 0)
                    continue;

                if (lines[span.Line] is not { } on)
                {
                    lines[span.Line] = on = [];
                    texts[span.Line] = document.LineAt(span.Line);
                }

                on.Add(new SyntaxSpan(span.Start, span.Length, span.Kind, span.Modifiers));
            }

            foreach (List<SyntaxSpan>? on in lines)
                on?.Sort((a, b) => a.Start.CompareTo(b.Start));

            semanticLines = lines;
            semanticTexts = texts;
        }
    }

    /// <summary>The names of a line, null when it has none or its text isn't what it was when they were given.</summary>
    private List<SyntaxSpan>? SemanticsOf(int line, string text)
    {
        if (semanticLines.Length == 0)
            return null;

        if (line < semanticTexts.Length && semanticLines[line] is { } here && string.Equals(semanticTexts[line], text, StringComparison.Ordinal))
            return here;

        int was = line - (document.LineCount - semanticLines.Length);
        return was >= 0 && was < semanticLines.Length && semanticLines[was] is { } moved && string.Equals(semanticTexts[was], text, StringComparison.Ordinal) ? moved : null;
    }

    /// <summary>
    /// The colours a line is drawn in, the grammar's (as the last colouring had them, or worked out for the line alone
    /// while it's typed into) with what its names are laid over them. Null while it can't be coloured at all.
    /// </summary>
    private List<SyntaxSpan>? DrawnColours(int line, string text)
    {
        List<SyntaxSpan>? under = ColoursOf(line, text) ?? FreshColours(line, text);
        if (under is null || !Highlight || SemanticsOf(line, text) is not { } over)
            return under;

        if (overlaid.TryGetValue(line, out var kept) && ReferenceEquals(kept.Under, under) && ReferenceEquals(kept.Over, over))
            return kept.Laid;

        if (kept.Laid is null && overlaid.Count >= MAX_OVERLAID)
            overlaid.Clear();

        List<SyntaxSpan> laid = kept.Laid ?? new List<SyntaxSpan>(under.Count + over.Count);
        SemanticOverlay.Overlay(under, over, laid);
        overlaid[line] = (under, over, laid);
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
