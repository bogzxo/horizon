using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// TODO and FIXME in a comment lit up, the word with the warning colour behind it (TODO) or the error colour (FIXME), and
// a mark down the minimap's edge for each, so what's left to do stands out of a page of comments. Only in a comment,
// a string or a name with TODO in it isn't one, and only as a word on its own (TODOS and NOTODO aren't). A file that
// isn't coloured as code (plain text) has every one lit, it's all comment there anyway.
public partial class CodeEditor
{
    // What's lit and how, the colour behind it is the skin's warning for one and error for the other
    private static readonly (string Word, bool Urgent)[] TodoWords = [("TODO", false), ("FIXME", true)];

    /// <summary>Whether TODO and FIXME in comments are lit up. On unless the program turns it off.</summary>
    public bool HighlightTodos { get; set; } = true;

    /// <summary>Helper method to light up the TODOs and FIXMEs of the lines in sight, behind their text.</summary>
    private void PaintTodos(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int firstLine, int lastLine)
    {
        if (!HighlightTodos)
            return;

        for (int line = firstLine; line <= lastLine && line < document.LineCount; line++)
        {
            if (IsHidden(line, out _))
                continue;

            foreach (var (from, to, urgent) in TodosOn(line))
            {
                Vector4 colour = (urgent ? skin.ErrorColor : skin.WarningColor) with { W = 0.28f };
                list.Rect(Piece(skin, scale, area, left, lineHeight, line, from, to), colour);
            }
        }
    }

    /// <summary>The TODOs and FIXMEs of a line that are in a comment, where each starts and ends and whether it's a FIXME.</summary>
    public IEnumerable<(int From, int To, bool Urgent)> TodosAt(int line) => TodosOn(line);

    private IEnumerable<(int From, int To, bool Urgent)> TodosOn(int line)
    {
        string text = document.LineAt(line);

        // Most lines have neither, which is all most lines cost
        if (!text.Contains("TODO", StringComparison.Ordinal) && !text.Contains("FIXME", StringComparison.Ordinal))
            yield break;

        List<SyntaxSpan>? spans = ColoursOf(line, text);
        foreach (var (word, urgent) in TodoWords)
        {
            for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal))
            {
                int end = at + word.Length;
                bool alone = (at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (end >= text.Length || !char.IsLetterOrDigit(text[end]));
                if (alone && InComment(spans, at))
                    yield return (at, end, urgent);
            }
        }
    }

    // Whether a place of a line is in a comment by its colours, everything is when the line isn't coloured as code
    private static bool InComment(List<SyntaxSpan>? spans, int at)
    {
        if (spans is null || spans.Count == 0)
            return true;

        bool anyComment = false;
        foreach (SyntaxSpan span in spans)
        {
            anyComment |= span.Kind == SyntaxKind.Comment;
            if (at >= span.Start && at < span.Start + span.Length)
                return span.Kind == SyntaxKind.Comment;
        }

        // Past the spans (white space a grammar left out), or a plain text file that's all names
        return !anyComment && spans.TrueForAll(span => span.Kind == SyntaxKind.Name);
    }
}
