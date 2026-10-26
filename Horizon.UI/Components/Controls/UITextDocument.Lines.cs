namespace Horizon.UI.Components;

// VS Code's keys for whole lines and for every place a word is. Shift+Alt+Up and Down copy the lines, Ctrl+L selects the
// caret's line and then one more each time, Ctrl+Shift+L puts a caret on every place the word (or the selection) is.
public sealed partial class UITextDocument
{
    // Past this many places Ctrl+Shift+L stops adding carets, a one letter selection in a big file is a lot of them
    private const int MAX_OCCURRENCE_CARETS = 10000;

    /// <summary>
    /// Shift+Alt+Down and Up, the lines of the selection (or the caret's) copied under themselves. Down the caret goes
    /// with the copy, up it stays on the lines above, which is the copy that went in over them. At every caret.
    /// </summary>
    public void CopyLines(int direction)
    {
        if (AtEveryCaret(() => CopyLines(direction), EditKind.Other))
            return;

        Record(EditKind.Other);
        var (first, last) = SelectedLines();
        int count = last - first + 1;
        lines.InsertRange(last + 1, lines.GetRange(first, count));
        LinesMoved?.Invoke(new UITextPosition(last, lines[last].Length), count);

        if (direction > 0)
        {
            Caret = Caret with { Line = Caret.Line + count };
            Anchor = Anchor with { Line = Anchor.Line + count };
        }

        preferredColumn = Caret.Column;
        Touched();
    }

    /// <summary>
    /// Ctrl+L, the caret's whole line selected, the end of the line and all, then the next line as well each time it's
    /// pressed again. A selection is widened to the whole lines it touches. At every caret.
    /// </summary>
    public void ExpandLineSelection()
    {
        if (AtEveryCaret(ExpandLineSelection, null))
            return;

        UITextPosition start = SelectionStart, end = SelectionEnd;

        // A selection of lines that ends at the start of one takes that one in next, one that ends inside a line the rest of it
        int next = end.Line + 1;
        if (HasSelection && end.Column == 0 && end.Line > start.Line && start.Column != 0)
            next = end.Line;

        UITextPosition to = next < lines.Count ? new UITextPosition(next, 0) : new UITextPosition(lines.Count - 1, lines[^1].Length);
        Select(new UITextPosition(start.Line, 0), to);
    }

    /// <summary>
    /// Ctrl+Shift+L, a caret on every place the selection is (on one line), or with nothing selected every place the word
    /// at the caret is as a word of its own, each with it selected. The caret that was there stays the one put down last.
    /// False when there's nothing to look for.
    /// </summary>
    public bool SelectAllOccurrences()
    {
        string query;
        bool wholeWord;
        UITextPosition here;
        if (!HasSelection)
        {
            var (start, end) = WordAt(Caret);
            if (end <= start)
                return false;

            query = lines[Caret.Line][start..end];
            wholeWord = true;
            here = new UITextPosition(Caret.Line, start);
        }
        else
        {
            if (SelectionStart.Line != SelectionEnd.Line)
                return false;

            query = SelectedText;
            wholeWord = false;
            here = SelectionStart;
        }

        var found = new List<(UITextSelection Selection, bool Primary)>();
        for (int line = 0; line < lines.Count && found.Count < MAX_OCCURRENCE_CARETS; line++)
        {
            string text = lines[line];
            for (int at = text.IndexOf(query, StringComparison.Ordinal); at >= 0; at = text.IndexOf(query, at + query.Length, StringComparison.Ordinal))
            {
                if (wholeWord && ((at > 0 && IsWordChar(text[at - 1])) || (at + query.Length < text.Length && IsWordChar(text[at + query.Length]))))
                    continue;

                var start = new UITextPosition(line, at);
                found.Add((new UITextSelection(start with { Column = at + query.Length }, start), start == here));
            }
        }

        if (found.Count == 0)
            return false;

        if (!found.Any(each => each.Primary))
            found[0] = (found[0].Selection, true);

        SetCarets(found);
        preferredColumn = Caret.Column;
        lastKind = EditKind.Other;
        return true;
    }
}
