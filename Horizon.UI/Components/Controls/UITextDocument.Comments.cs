namespace Horizon.UI.Components;

// Comments, Ctrl+/ and Shift+Alt+A, in whatever the language writes them with (LineComment and BlockComment, which the
// code editor sets from CodeComments as its language changes). A language with no line comment (HTML, CSS) gets its
// lines wrapped in a block comment by Ctrl+/ instead, the way VS Code does it.
public sealed partial class UITextDocument
{
    /// <summary>
    /// The lines of the selection (or the caret's) made comments, or made code again if every one of them is a comment
    /// already. The comment goes in at the indentation of the least indented of them. A language with no line comment
    /// has the lines wrapped in its block comment instead, or unwrapped when they already are.
    /// </summary>
    public void ToggleComment()
    {
        if (LineComment.Length == 0)
        {
            if (BlockComment.Start.Length > 0 && BlockComment.End.Length > 0)
                ToggleLinesAsBlock();
            return;
        }

        if (AtEveryCaret(ToggleComment, EditKind.Other))
            return;

        Record(EditKind.Other);
        var (first, last) = SelectedLines();
        string mark = LineComment + " ";

        bool allComments = true;
        int indent = int.MaxValue;
        for (int line = first; line <= last; line++)
        {
            string text = lines[line];
            if (text.Trim().Length == 0) continue;
            indent = Math.Min(indent, text.Length - text.TrimStart().Length);
            if (!text.TrimStart().StartsWith(LineComment)) allComments = false;
        }

        if (indent == int.MaxValue)
            return;

        int caretShift = 0, anchorShift = 0;
        for (int line = first; line <= last; line++)
        {
            string text = lines[line];
            if (text.Trim().Length == 0) continue;

            int shift;
            if (allComments)
            {
                int at = text.IndexOf(LineComment, StringComparison.Ordinal);
                int length = text.AsSpan(at).StartsWith(mark) ? mark.Length : LineComment.Length;
                lines[line] = text.Remove(at, length);
                shift = -length;
            }
            else
            {
                lines[line] = text.Insert(indent, mark);
                shift = mark.Length;
            }

            if (line == Caret.Line) caretShift = shift;
            if (line == Anchor.Line) anchorShift = shift;
        }

        Caret = Clamp(Caret with { Column = Math.Max(0, Caret.Column + caretShift) });
        Anchor = Clamp(Anchor with { Column = Math.Max(0, Anchor.Column + anchorShift) });
        preferredColumn = Caret.Column;
        Touched();
    }

    /// <summary>
    /// Shift+Alt+A, the selection put inside a block comment, or taken out of the one it is (or sits just inside of).
    /// Nothing selected, the comment the caret is in on its line is taken off, or an empty one goes in with the caret in
    /// its middle. A language with no block comment gets its line comment instead.
    /// </summary>
    public void ToggleBlockComment()
    {
        var (open, close) = BlockComment;
        if (open.Length == 0 || close.Length == 0)
        {
            ToggleComment();
            return;
        }

        if (AtEveryCaret(ToggleBlockComment, EditKind.Other))
            return;

        Record(EditKind.Other);

        if (HasSelection)
        {
            UITextPosition start = SelectionStart, end = SelectionEnd;
            string selected = SelectedText;
            string before = lines[start.Line][..start.Column], after = lines[end.Line][end.Column..];

            if (selected.Length >= open.Length + close.Length && selected.StartsWith(open, StringComparison.Ordinal) && selected.EndsWith(close, StringComparison.Ordinal))
            {
                Unwrap(start, end);
            }
            else if (before.TrimEnd().EndsWith(open, StringComparison.Ordinal) && after.TrimStart().StartsWith(close, StringComparison.Ordinal))
            {
                int openAt = before.TrimEnd().Length - open.Length;
                int closeEnd = end.Column + (after.Length - after.TrimStart().Length) + close.Length;
                Unwrap(new UITextPosition(start.Line, openAt), new UITextPosition(end.Line, closeEnd));
            }
            else
            {
                Wrap(start, end);
            }
        }
        else
        {
            string text = lines[Caret.Line];
            int openAt = text[..Caret.Column].LastIndexOf(open, StringComparison.Ordinal);
            int closeAt = openAt < 0 ? -1 : text.IndexOf(close, Math.Max(Caret.Column, openAt + open.Length), StringComparison.Ordinal);
            bool inside = openAt >= 0 && closeAt >= 0 && text.IndexOf(close, openAt + open.Length, StringComparison.Ordinal) == closeAt;

            if (inside)
            {
                Unwrap(new UITextPosition(Caret.Line, openAt), new UITextPosition(Caret.Line, closeAt + close.Length));
            }
            else
            {
                int at = Caret.Column;
                lines[Caret.Line] = text.Insert(at, open + "  " + close);
                Caret = Anchor = Caret with { Column = at + open.Length + 1 };
            }
        }

        preferredColumn = Caret.Column;
        Touched();
    }

    // Ctrl+/ in a language with only block comments, the whole lines (from the first thing on the first to the last
    // thing on the last) wrapped in one, or unwrapped when that's what they are already
    private void ToggleLinesAsBlock()
    {
        if (AtEveryCaret(ToggleLinesAsBlock, EditKind.Other))
            return;

        var (first, last) = SelectedLines();
        while (first < last && lines[first].Trim().Length == 0) first++;
        while (last > first && lines[last].Trim().Length == 0) last--;
        if (lines[first].Trim().Length == 0)
            return;

        Record(EditKind.Other);
        var (open, close) = BlockComment;
        string top = lines[first], bottom = lines[last];
        var start = new UITextPosition(first, top.Length - top.TrimStart().Length);
        var end = new UITextPosition(last, bottom.TrimEnd().Length);
        bool wrapped = top.AsSpan(start.Column).StartsWith(open) && bottom.AsSpan(0, end.Column).EndsWith(close)
            && (first != last || end.Column - start.Column >= open.Length + close.Length);

        if (wrapped)
            Unwrap(start, end);
        else
            Wrap(start, end);

        preferredColumn = Caret.Column;
        Touched();
    }

    // The block comment's marks put round the text from one place to another, a space inside each. What was selected
    // stays selected, inside of them
    private void Wrap(UITextPosition start, UITextPosition end)
    {
        var (open, close) = BlockComment;
        InsertOnLine(end.Line, end.Column, " " + close, pushesAt: false);
        InsertOnLine(start.Line, start.Column, open + " ", pushesAt: true);
    }

    // The marks taken off a comment that runs from the start of its opening mark to the end of its closing one, the
    // space inside each with them
    private void Unwrap(UITextPosition start, UITextPosition end)
    {
        var (open, close) = BlockComment;
        int from = end.Column - close.Length;
        if (from > 0 && lines[end.Line][from - 1] == ' ' && (end.Line != start.Line || from - 1 >= start.Column + open.Length))
            from--;
        RemoveOnLine(end.Line, from, end.Column - from);

        string top = lines[start.Line];
        int length = open.Length;
        if (start.Column + length < top.Length && top[start.Column + length] == ' ')
            length++;
        RemoveOnLine(start.Line, start.Column, length);
    }

    // Text put into a line, the carets on it after the place moved along with it (and the one right at it as well, when asked)
    private void InsertOnLine(int line, int column, string text, bool pushesAt)
    {
        lines[line] = lines[line].Insert(column, text);
        UITextPosition Pushed(UITextPosition at) =>
            at.Line == line && (at.Column > column || (pushesAt && at.Column == column)) ? at with { Column = at.Column + text.Length } : at;
        Caret = Pushed(Caret);
        Anchor = Pushed(Anchor);
    }

    // Text taken out of a line, a caret inside of what went left where it was
    private void RemoveOnLine(int line, int column, int length)
    {
        lines[line] = lines[line].Remove(column, length);
        UITextPosition Pulled(UITextPosition at) =>
            at.Line == line && at.Column > column ? at with { Column = Math.Max(column, at.Column - length) } : at;
        Caret = Pulled(Caret);
        Anchor = Pulled(Anchor);
    }
}
