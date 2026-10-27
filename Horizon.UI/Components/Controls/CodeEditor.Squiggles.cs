using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// What's wrong, drawn under the code it's about. An error or a warning a thin wavy line in its colour under the whole of
// the word it covers, VS Code's squiggle, a hint a row of dots, and something obsolete struck through the middle of its
// letters. They were flat bars three pixels thick, heavier than any other line under the code, a strike went through
// the tops of the letters, and a mistake given with no length (a string never closed) was a bar under its quote.
public partial class CodeEditor
{
    // How tall a squiggle is from crest to trough and how thick its line is, shares of a line's height. Three pixels and
    // one at VS Code's size, scaled with the font from there
    private const float SQUIGGLE_HEIGHT = 0.16f, SQUIGGLE_THICKNESS = 0.055f;

    // How far up from where the letters sit the middle of a small letter is, a share of a line's height
    private const float LETTER_MIDDLE = 0.2f;

    private void PaintDiagnostic(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, in UICodeDiagnostic diagnostic, int firstLine, int lastLine)
    {
        if (diagnostic.Line < firstLine || diagnostic.Line > lastLine || diagnostic.Line >= document.LineCount || IsHidden(diagnostic.Line, out _))
            return;

        var (column, endColumn) = DiagnosticSpan(diagnostic);
        UIRect piece = Piece(skin, scale, area, left, lineHeight, diagnostic.Line, column, endColumn);
        float to = MathF.Max(piece.Min.X + lineHeight * 0.4f, piece.Max.X);
        float thickness = MathF.Max(1.0f, lineHeight * SQUIGGLE_THICKNESS);

        // Something obsolete struck through, whatever else it's marked with
        if (diagnostic.Deprecated)
            Strike(list, piece.Min.X, piece.Max.X, StrikeY(skin, scale, piece.Max.Y, lineHeight), skin.TextColor with { W = skin.TextColor.W * 0.7f });

        // A hint is a line of dots in the text's colour, seen through. Always as many quads for a diagnostic of a kind,
        // the dots a few of them under the whole of it
        if (diagnostic.Hint)
        {
            Vector4 quiet = skin.TextColor with { W = skin.TextColor.W * 0.45f };
            float dot = MathF.Max(1.0f, MathF.Round(thickness * 1.5f)), step = dot * 2.5f;
            for (float x = piece.Min.X; x + dot <= to; x += step)
                list.Rect(new UIRect(new Vector2(x, piece.Min.Y + 1.0f), new Vector2(x + dot, piece.Min.Y + 1.0f + dot)), quiet);
            return;
        }

        // Its middle as low in the row as it goes with all of it still in the row, under the letters that hang down
        float height = MathF.Max(3.0f, lineHeight * SQUIGGLE_HEIGHT);
        Vector4 shade = diagnostic.Warning ? skin.WarningColor : skin.ErrorColor;
        list.Squiggle(piece.Min.X, to, piece.Min.Y + height * 0.5f + 0.5f, height, thickness, shade);
    }

    /// <summary>
    /// Where what's wrong starts and ends on its line. One given with no length covers the word it's in (VS Code's way),
    /// the word before it at the end of a line, the string a quote opens, and the run of what isn't a space from it
    /// otherwise. A string never closed has all of it marked and not its quote.
    /// </summary>
    private (int From, int To) DiagnosticSpan(in UICodeDiagnostic diagnostic)
    {
        string text = diagnostic.Line < document.LineCount ? document.LineAt(diagnostic.Line) : string.Empty;
        int from = Math.Clamp(diagnostic.Column, 0, text.Length);
        if (diagnostic.Length > 0)
            return (from, Math.Min(text.Length, from + diagnostic.Length));

        // At the end of the line, the word before it
        if (from == text.Length)
        {
            int start = from;
            while (start > 0 && IsWordChar(text[start - 1])) start--;
            return start < from ? (start, from) : (Math.Max(0, from - 1), from);
        }

        if (IsWordChar(text[from]))
        {
            int start = from, end = from;
            while (start > 0 && IsWordChar(text[start - 1])) start--;
            while (end < text.Length && IsWordChar(text[end])) end++;
            return (start, end);
        }

        // On a quote, the string it opens up to where it's closed or the end of the line
        if (text[from] is '"' or '\'' or '`')
        {
            int closed = text.IndexOf(text[from], from + 1), end = text.Length;
            while (end > from + 1 && char.IsWhiteSpace(text[end - 1])) end--;
            return (from, closed < 0 ? end : closed + 1);
        }

        int stop = from;
        while (stop < text.Length && !char.IsWhiteSpace(text[stop])) stop++;
        return (from, Math.Max(from + 1, stop));
    }

    /// <summary>The columns of its line something wrong is drawn under, from one up to (not at) another. See <see cref="DiagnosticSpan"/>.</summary>
    public (int From, int To) UnderlinedColumns(UICodeDiagnostic diagnostic) => DiagnosticSpan(diagnostic);

    // Whether the pointer is on what's wrong, the whole of what's drawn under and the place just past it
    private bool Covers(in UICodeDiagnostic diagnostic, UITextPosition at)
    {
        if (at.Line != diagnostic.Line)
            return false;

        var (from, to) = DiagnosticSpan(diagnostic);
        return at.Column >= from && at.Column <= to;
    }

    /// <summary>How high a strike through goes, through the middle of the small letters of a row from its top.</summary>
    private float StrikeY(UISkin skin, float scale, float top, float lineHeight) =>
        MathF.Round(top - FontOf(skin).Baseline * scale + lineHeight * LETTER_MIDDLE);

    /// <summary>A strike through from one place to another, a single thin line whatever the size.</summary>
    private static void Strike(UIDrawList list, float from, float to, float y, Vector4 colour)
    {
        if (to > from)
            list.Rect(new UIRect(new Vector2(from, y), new Vector2(to, y + 1.0f)), colour);
    }
}
