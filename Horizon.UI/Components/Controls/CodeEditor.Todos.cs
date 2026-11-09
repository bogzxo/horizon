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
        if (!MightHaveTodo(text))
            return [];

        // Plain text is coloured all one name, which says nothing of where a comment is, it's all comment
        if (!Highlight)
            return TodosIn(text, null);

        // A line typed into is coloured on its own until the rest catches up. One that can't be yet (it starts inside a
        // comment over several lines the last colouring never saw) waits for its colours, a string lit for a moment
        // because nothing said it wasn't a comment is worse than a TODO lit a moment late
        List<SyntaxSpan>? spans = ColoursOf(line, text) ?? FreshColours(line, text);
        return spans is null ? [] : TodosIn(text, spans);
    }

    /// <summary>
    /// The TODOs and FIXMEs of a line of text that are in a comment by the line's colours, where each starts and ends and
    /// whether it's a FIXME, the TODOs first. No colours at all is plain text, where every one is lit.
    /// </summary>
    public static IEnumerable<(int From, int To, bool Urgent)> TodosIn(string text, IReadOnlyList<SyntaxSpan>? spans)
    {
        if (!MightHaveTodo(text))
            yield break;

        foreach (var (word, urgent) in TodoWords)
        {
            for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal))
            {
                // A word on its own, an underscore is part of a name the way a letter is (MY_TODO, TODO_LIST)
                int end = at + word.Length;
                bool alone = (at == 0 || !IsWordChar(text[at - 1])) && (end >= text.Length || !IsWordChar(text[end]));
                if (alone && InComment(spans, at))
                    yield return (at, end, urgent);
            }
        }
    }

    private static bool MightHaveTodo(string text) =>
        text.Contains("TODO", StringComparison.Ordinal) || text.Contains("FIXME", StringComparison.Ordinal);

    // Whether a place of a line is in a comment by its colours, everything is when the line isn't coloured as code
    private static bool InComment(IReadOnlyList<SyntaxSpan>? spans, int at)
    {
        if (spans is null || spans.Count == 0)
            return true;

        bool anyComment = false, allNames = true;
        foreach (SyntaxSpan span in spans)
        {
            anyComment |= span.Kind == SyntaxKind.Comment;
            allNames &= span.Kind == SyntaxKind.Name;
            if (at >= span.Start && at < span.Start + span.Length)
                return span.Kind == SyntaxKind.Comment;
        }

        // Past the spans (white space a grammar left out), or a plain text file that's all names
        return !anyComment && allNames;
    }
}
