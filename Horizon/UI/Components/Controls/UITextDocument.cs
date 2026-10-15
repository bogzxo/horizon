using System.Text;

namespace Horizon.UI.Components;

/// <summary>A place in a text of lines, which line and how far along it, both counted from 0.</summary>
public readonly record struct UITextPosition(int Line, int Column) : IComparable<UITextPosition>
{
    public int CompareTo(UITextPosition other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    public static bool operator <(UITextPosition a, UITextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(UITextPosition a, UITextPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(UITextPosition a, UITextPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(UITextPosition a, UITextPosition b) => a.CompareTo(b) >= 0;
}

/// <summary>
/// Text of many lines being edited, with a caret, a selection and an undo history, and nothing to do with how any of
/// it is drawn. What a <see cref="CodeEditor"/> edits, kept apart so the editing can be tested without a window.
/// <para>
/// It edits the way a code editor does. Enter keeps the indentation of the line it leaves (and goes one in after an
/// opening bracket), tab indents the lines of a selection and shift tab takes them back out, brackets and quotes
/// close themselves and typing the closing one steps over it, home goes to the first thing on the line before the
/// start of it, and the undo history takes a run of typing as one step.
/// </para>
/// </summary>
public sealed class UITextDocument
{
    // What one step of the undo history comes back to
    private readonly record struct Snapshot(string Text, UITextPosition Caret, UITextPosition Anchor);

    // How a step was made, a run of the same kind of edit going in as one step
    private enum EditKind
    {
        Other,
        Typing,
        Deleting
    }

    private const int MAX_UNDO = 300;

    private readonly List<string> lines = [string.Empty];

    // The lines joined up, made once and kept until they change. Everybody wants the whole text (the colours, the parse,
    // the outline, every step of the undo) and joining 2000 lines for each of them was a hundred kilobytes a time. Every
    // edit goes through Record before it touches a line, which lets go of it, and Touched once it is done
    private string? joined;
    private readonly List<Snapshot> undo = [];
    private readonly List<Snapshot> redo = [];
    private EditKind lastKind = EditKind.Other;

    // The column up and down try to keep to, the one the caret was put at last by anything that wasn't up or down
    private int preferredColumn;

    /// <summary>What one level of indentation is.</summary>
    public string Indent { get; set; } = "    ";

    /// <summary>What a line comment starts with, for <see cref="ToggleComment"/>.</summary>
    public string LineComment { get; set; } = "//";

    /// <summary>Whether brackets and quotes close themselves as they are typed.</summary>
    public bool ClosePairs { get; set; } = true;

    /// <summary>Where typing happens.</summary>
    public UITextPosition Caret { get; private set; }

    /// <summary>Where the selection started, everything between this and the caret is selected.</summary>
    public UITextPosition Anchor { get; private set; }

    /// <summary>Goes up by one with every change of the text, for whoever keeps something worked out of it.</summary>
    public int Version { get; private set; }

    /// <summary>Called after every change of the text, not of the caret.</summary>
    public event Action? Changed;

    public int LineCount => lines.Count;

    public string LineAt(int line) => lines[line];

    public bool HasSelection => Caret != Anchor;
    public UITextPosition SelectionStart => Caret < Anchor ? Caret : Anchor;
    public UITextPosition SelectionEnd => Caret < Anchor ? Anchor : Caret;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>
    /// The whole text, its lines ended with '\n'. Setting it starts over, the caret at the top and no history.
    /// </summary>
    public string Text
    {
        get => joined ??= string.Join('\n', lines);
        set
        {
            SetLines(value ?? string.Empty);
            Caret = Anchor = default;
            preferredColumn = 0;
            undo.Clear();
            redo.Clear();
            lastKind = EditKind.Other;
            Touched();
        }
    }

    /// <summary>Replaces the whole text as one step that can be undone, keeping the caret where it can.</summary>
    public void Replace(string text)
    {
        Record(EditKind.Other);
        SetLines(text ?? string.Empty);
        Caret = Anchor = Clamp(Caret);
        Touched();
    }

    private void SetLines(string text)
    {
        joined = null;
        lines.Clear();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            lines.Add(line);
    }

    /// <summary>What is selected, its lines ended with '\n'.</summary>
    public string SelectedText => TextBetween(SelectionStart, SelectionEnd);

    public string TextBetween(UITextPosition from, UITextPosition to)
    {
        if (from.Line == to.Line)
            return lines[from.Line][from.Column..to.Column];

        var built = new StringBuilder();
        built.Append(lines[from.Line], from.Column, lines[from.Line].Length - from.Column);
        for (int line = from.Line + 1; line < to.Line; line++)
            built.Append('\n').Append(lines[line]);
        built.Append('\n').Append(lines[to.Line], 0, to.Column);
        return built.ToString();
    }

    /// <summary>Keeps a place inside of the text.</summary>
    public UITextPosition Clamp(UITextPosition position)
    {
        int line = Math.Clamp(position.Line, 0, lines.Count - 1);
        return new UITextPosition(line, Math.Clamp(position.Column, 0, lines[line].Length));
    }

    /* The caret */

    /// <summary>Puts the caret somewhere, taking the selection along or dropping it.</summary>
    public void MoveTo(UITextPosition position, bool selecting = false)
    {
        Caret = Clamp(position);
        if (!selecting)
            Anchor = Caret;

        preferredColumn = Caret.Column;
        lastKind = EditKind.Other;
    }

    /// <summary>Selects from one place to another, the caret at the second.</summary>
    public void Select(UITextPosition from, UITextPosition to)
    {
        Anchor = Clamp(from);
        Caret = Clamp(to);
        preferredColumn = Caret.Column;
        lastKind = EditKind.Other;
    }

    public void SelectAll() => Select(default, new UITextPosition(lines.Count - 1, lines[^1].Length));

    /// <summary>A character to the left or the right, over the end of a line onto the next. Without shift a selection is left at the end the arrow points at.</summary>
    public void MoveHorizontally(int direction, bool selecting)
    {
        if (!selecting && HasSelection)
        {
            MoveTo(direction < 0 ? SelectionStart : SelectionEnd);
            return;
        }

        MoveTo(Step(Caret, direction), selecting);
    }

    /// <summary>Lines up or down, keeping to the column the caret was last put at. Past the first or last line it goes to that end of it.</summary>
    public void MoveVertically(int by, bool selecting)
    {
        int line = Caret.Line + by;
        int keep = preferredColumn;

        UITextPosition to = line < 0 ? default
            : line >= lines.Count ? new UITextPosition(lines.Count - 1, lines[^1].Length)
            : new UITextPosition(line, Math.Min(keep, lines[line].Length));

        Caret = to;
        if (!selecting)
            Anchor = Caret;

        // Up and down remember where they started out from, the ends of the text aside
        preferredColumn = line < 0 || line >= lines.Count ? Caret.Column : keep;
        lastKind = EditKind.Other;
    }

    /// <summary>A word to the left or the right, across the spaces next to it and then across the word.</summary>
    public void MoveWord(int direction, bool selecting) => MoveTo(WordFrom(Caret, direction), selecting);

    /// <summary>The first thing on the line that isn't a space, or the start of the line if the caret is there already.</summary>
    public void MoveHome(bool selecting)
    {
        string line = lines[Caret.Line];
        int first = line.Length - line.TrimStart().Length;
        MoveTo(new UITextPosition(Caret.Line, Caret.Column == first ? 0 : first), selecting);
    }

    public void MoveEnd(bool selecting) => MoveTo(new UITextPosition(Caret.Line, lines[Caret.Line].Length), selecting);

    public void MoveToStart(bool selecting) => MoveTo(default, selecting);

    public void MoveToEnd(bool selecting) => MoveTo(new UITextPosition(lines.Count - 1, lines[^1].Length), selecting);

    /// <summary>Selects the word at a place, or the run of whatever else is there.</summary>
    public void SelectWordAt(UITextPosition at)
    {
        at = Clamp(at);
        var (start, end) = RunAt(at);
        Select(new UITextPosition(at.Line, start), new UITextPosition(at.Line, end));
    }

    /// <summary>
    /// Selects whole words from the word a double click picked (<paramref name="from"/> to <paramref name="to"/>) to the
    /// word at a place, either way, which is what dragging after a double click does. The word first picked stays in.
    /// </summary>
    public void SelectWordsTo(UITextPosition from, UITextPosition to, UITextPosition at)
    {
        at = Clamp(at);
        var (start, end) = RunAt(at);
        var wordStart = new UITextPosition(at.Line, start);
        var wordEnd = new UITextPosition(at.Line, end);
        if (at < from)
            Select(to, wordStart);
        else
            Select(from, wordEnd > to ? wordEnd : to);
    }

    /// <summary>The word at a place, or the run of whatever else that isn't a space, where it starts and ends on its line.</summary>
    private (int Start, int End) RunAt(UITextPosition at)
    {
        string line = lines[at.Line];
        if (line.Length == 0)
            return (0, 0);

        int probe = Math.Min(at.Column, line.Length - 1);
        bool word = IsWordChar(line[probe]);
        int start = probe, end = probe;

        while (start > 0 && IsWordChar(line[start - 1]) == word && !char.IsWhiteSpace(line[start - 1])) start--;
        while (end < line.Length && IsWordChar(line[end]) == word && !char.IsWhiteSpace(line[end])) end++;
        return (start, end);
    }

    /// <summary>The word at a place, where it starts and ends on its line, both the same for none.</summary>
    public (int Start, int End) WordAt(UITextPosition at)
    {
        at = Clamp(at);
        string line = lines[at.Line];
        int start = at.Column, end = at.Column;
        while (start > 0 && IsWordChar(line[start - 1])) start--;
        while (end < line.Length && IsWordChar(line[end])) end++;
        return (start, end);
    }

    /// <summary>The word the caret is in or just after, empty for none, for whoever completes words.</summary>
    public string WordBeforeCaret()
    {
        string line = lines[Caret.Line];
        int start = Caret.Column;
        while (start > 0 && IsWordChar(line[start - 1])) start--;
        return line[start..Caret.Column];
    }

    public static bool IsWordChar(char character) => char.IsLetterOrDigit(character) || character == '_';

    private UITextPosition Step(UITextPosition origin, int direction)
    {
        if (direction < 0)
        {
            if (origin.Column > 0) return origin with { Column = origin.Column - 1 };
            return origin.Line > 0 ? new UITextPosition(origin.Line - 1, lines[origin.Line - 1].Length) : origin;
        }

        if (origin.Column < lines[origin.Line].Length) return origin with { Column = origin.Column + 1 };
        return origin.Line + 1 < lines.Count ? new UITextPosition(origin.Line + 1, 0) : origin;
    }

    private UITextPosition WordFrom(UITextPosition from, int direction)
    {
        UITextPosition at = from;

        // Over the end of a line it is one step, the way every editor does it
        if (direction < 0 && at.Column == 0) return Step(at, -1);
        if (direction > 0 && at.Column == lines[at.Line].Length) return Step(at, 1);

        string line = lines[at.Line];
        int column = at.Column;

        if (direction < 0)
        {
            while (column > 0 && char.IsWhiteSpace(line[column - 1])) column--;
            if (column > 0)
            {
                bool word = IsWordChar(line[column - 1]);
                while (column > 0 && !char.IsWhiteSpace(line[column - 1]) && IsWordChar(line[column - 1]) == word) column--;
            }
        }
        else
        {
            if (column < line.Length && !char.IsWhiteSpace(line[column]))
            {
                bool word = IsWordChar(line[column]);
                while (column < line.Length && !char.IsWhiteSpace(line[column]) && IsWordChar(line[column]) == word) column++;
            }
            while (column < line.Length && char.IsWhiteSpace(line[column])) column++;
        }

        return at with { Column = column };
    }

    /* Editing */

    private static readonly Dictionary<char, char> Pairs = new() { ['('] = ')', ['['] = ']', ['{'] = '}', ['"'] = '"' };

    /// <summary>Types a character where the caret is, over what is selected.</summary>
    public void Type(char character)
    {
        if (character == '\n')
        {
            NewLine();
            return;
        }

        string line = lines[Caret.Line];
        char next = Caret.Column < line.Length ? line[Caret.Column] : '\0';

        // Typing the closing half of a pair that is already there steps over it
        if (ClosePairs && !HasSelection && next == character && character is ')' or ']' or '}' or '"')
        {
            MoveTo(Caret with { Column = Caret.Column + 1 });
            lastKind = EditKind.Typing;
            return;
        }

        // An opening one brings its closing one along, round a selection or with nothing in between. Not in front of
        // a word, where the closing one is usually somewhere else
        if (ClosePairs && Pairs.TryGetValue(character, out char closing) && (HasSelection || next == '\0' || !IsWordChar(next)) && !(character == '"' && InsideWordBefore()))
        {
            Record(EditKind.Other);

            if (HasSelection && SelectionStart.Line == SelectionEnd.Line)
            {
                UITextPosition start = SelectionStart, end = SelectionEnd;
                InsertRaw(end, closing.ToString());
                InsertRaw(start, character.ToString());
                Select(start with { Column = start.Column + 1 }, end with { Column = end.Column + 1 });
            }
            else
            {
                DeleteSelectionRaw();
                InsertRaw(Caret, string.Concat(character, closing));
                MoveTo(Caret with { Column = Caret.Column + 1 });
            }

            lastKind = EditKind.Typing;
            Touched();
            return;
        }

        Record(EditKind.Typing);
        DeleteSelectionRaw();
        UITextPosition after = InsertRaw(Caret, character.ToString());
        MoveTo(after);
        lastKind = EditKind.Typing;

        // A closing bracket typed on a line of nothing but indentation goes back out a level
        if (character is '}' or ']' or ')' && lines[Caret.Line].TrimStart() == character.ToString())
            Outdent();

        Touched();
    }

    // Whether the character before the caret is part of a word, where a quote is far more likely to be the end of something
    private bool InsideWordBefore() => Caret.Column > 0 && IsWordChar(lines[Caret.Line][Caret.Column - 1]);

    /// <summary>Puts text where the caret is, over what is selected, lines and all, as one step.</summary>
    public void Insert(string text)
    {
        Record(EditKind.Other);
        DeleteSelectionRaw();
        MoveTo(InsertRaw(Caret, text.Replace("\r\n", "\n").Replace('\r', '\n')));
        Touched();
    }

    /// <summary>
    /// A new line, as indented as the one the caret is on, a level further after an opening bracket, and with the
    /// closing one on a line of its own if the caret was right between the two.
    /// </summary>
    public void NewLine()
    {
        Record(EditKind.Other);
        DeleteSelectionRaw();

        string line = lines[Caret.Line];
        string indent = line[..(line.Length - line.TrimStart().Length)];
        if (indent.Length > Caret.Column) indent = indent[..Caret.Column];

        char before = PreviousNonSpace(line, Caret.Column);
        char after = Caret.Column < line.Length ? line[Caret.Column] : '\0';
        bool opens = before is '{' or '[' or '(';

        if (opens && after is '}' or ']' or ')')
        {
            UITextPosition inside = InsertRaw(Caret, "\n" + indent + Indent);
            InsertRaw(inside, "\n" + indent);
            MoveTo(inside);
        }
        else
        {
            // The spaces the caret leaves behind it at the end of the line go, they would only be trailing
            string rest = line[Caret.Column..];
            MoveTo(InsertRaw(Caret, "\n" + indent + (opens ? Indent : string.Empty)));
            if (rest.Length > 0 && rest.TrimStart().Length != rest.Length)
            {
                int spaces = rest.Length - rest.TrimStart().Length;
                lines[Caret.Line] = lines[Caret.Line].Remove(Caret.Column, spaces);
            }
        }

        lastKind = EditKind.Other;
        Touched();
    }

    private static char PreviousNonSpace(string line, int column)
    {
        for (int i = column - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(line[i]))
                return line[i];
        }

        return '\0';
    }

    /// <summary>What is selected goes, or failing that the character before the caret (the pair round it if it is an empty one, a level of indentation in the indentation).</summary>
    public void Backspace()
    {
        Record(EditKind.Deleting);

        if (!DeleteSelectionRaw())
        {
            string line = lines[Caret.Line];
            int column = Caret.Column;

            if (column > 0 && column < line.Length && Pairs.TryGetValue(line[column - 1], out char closing) && line[column] == closing)
            {
                DeleteRaw(Caret with { Column = column - 1 }, Caret with { Column = column + 1 });
                MoveTo(Caret with { Column = column - 1 });
            }
            else if (column > 0 && line[..column].Trim().Length == 0)
            {
                // In the indentation a whole level goes at once
                int to = Math.Max(0, (column - 1) / Indent.Length * Indent.Length);
                DeleteRaw(Caret with { Column = to }, Caret);
                MoveTo(Caret with { Column = to });
            }
            else
            {
                UITextPosition from = Step(Caret, -1);
                DeleteRaw(from, Caret);
                MoveTo(from);
            }
        }

        lastKind = EditKind.Deleting;
        Touched();
    }

    /// <summary>What is selected goes, or failing that the character after the caret.</summary>
    public void Delete()
    {
        Record(EditKind.Deleting);
        if (!DeleteSelectionRaw())
            DeleteRaw(Caret, Step(Caret, 1));
        MoveTo(Caret);
        lastKind = EditKind.Deleting;
        Touched();
    }

    /// <summary>What is selected goes, or failing that the word before (or after) the caret.</summary>
    public void DeleteWord(int direction)
    {
        Record(EditKind.Other);
        if (!DeleteSelectionRaw())
        {
            UITextPosition to = WordFrom(Caret, direction);
            UITextPosition from = direction < 0 ? to : Caret;
            DeleteRaw(from, direction < 0 ? Caret : to);
            MoveTo(from);
        }

        Touched();
    }

    /// <summary>
    /// Tab. A selection over more than one line has every line of it indented a level, otherwise spaces to the next
    /// stop of the indentation go in where the caret is.
    /// </summary>
    public void IndentLines()
    {
        if (HasSelection && SelectionStart.Line != SelectionEnd.Line)
        {
            Record(EditKind.Other);
            var (first, last) = SelectedLines();
            for (int line = first; line <= last; line++)
            {
                if (lines[line].Length > 0)
                    lines[line] = Indent + lines[line];
            }

            Shift(first, last, Indent.Length);
            Touched();
            return;
        }

        int column = Caret.Column;
        int spaces = Indent.Length - column % Indent.Length;
        Insert(new string(' ', spaces));
    }

    /// <summary>Shift with tab, every line of the selection (or the caret's) a level back out, as far as it can go.</summary>
    public void Outdent()
    {
        Record(EditKind.Other);
        var (first, last) = SelectedLines();
        int caretTaken = 0, anchorTaken = 0;

        for (int line = first; line <= last; line++)
        {
            string text = lines[line];
            int take = 0;
            while (take < Indent.Length && take < text.Length && text[take] == ' ') take++;
            if (take == 0 && text.StartsWith('\t')) take = 1;

            lines[line] = text[take..];
            if (line == Caret.Line) caretTaken = take;
            if (line == Anchor.Line) anchorTaken = take;
        }

        Caret = Clamp(Caret with { Column = Math.Max(0, Caret.Column - caretTaken) });
        Anchor = Clamp(Anchor with { Column = Math.Max(0, Anchor.Column - anchorTaken) });
        preferredColumn = Caret.Column;
        Touched();
    }

    /// <summary>
    /// The lines of the selection (or the caret's) made comments, or made code again if every one of them is a comment
    /// already. The comment goes in at the indentation of the least indented of them.
    /// </summary>
    public void ToggleComment()
    {
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

    /// <summary>The selection once more after itself, or the caret's line once more under itself.</summary>
    public void Duplicate()
    {
        Record(EditKind.Other);

        if (HasSelection)
        {
            string selected = SelectedText;
            UITextPosition end = SelectionEnd;
            UITextPosition after = InsertRaw(end, selected);
            Select(end, after);
        }
        else
        {
            lines.Insert(Caret.Line + 1, lines[Caret.Line]);
            Caret = Anchor = Caret with { Line = Caret.Line + 1 };
        }

        Touched();
    }

    /// <summary>The lines of the selection (or the caret's) swapped with the line above or below them.</summary>
    public void MoveLines(int direction)
    {
        var (first, last) = SelectedLines();
        if ((direction < 0 && first == 0) || (direction > 0 && last == lines.Count - 1))
            return;

        Record(EditKind.Other);

        if (direction < 0)
        {
            string above = lines[first - 1];
            lines.RemoveAt(first - 1);
            lines.Insert(last, above);
        }
        else
        {
            string below = lines[last + 1];
            lines.RemoveAt(last + 1);
            lines.Insert(first, below);
        }

        Caret = Caret with { Line = Caret.Line + direction };
        Anchor = Anchor with { Line = Anchor.Line + direction };
        Touched();
    }

    // The lines a selection touches, a selection ending at the very start of a line leaving that line out
    private (int First, int Last) SelectedLines()
    {
        UITextPosition start = SelectionStart, end = SelectionEnd;
        int last = end.Line > start.Line && end.Column == 0 ? end.Line - 1 : end.Line;
        return (start.Line, last);
    }

    private void Shift(int first, int last, int by)
    {
        if (Caret.Line >= first && Caret.Line <= last && lines[Caret.Line].Length > 0) Caret = Caret with { Column = Caret.Column + by };
        if (Anchor.Line >= first && Anchor.Line <= last && lines[Anchor.Line].Length > 0) Anchor = Anchor with { Column = Anchor.Column + by };
        Caret = Clamp(Caret);
        Anchor = Clamp(Anchor);
        preferredColumn = Caret.Column;
    }

    /* Finding */

    /// <summary>
    /// Selects the next place a piece of text is found after the caret (or before it, going back), round past the end.
    /// </summary>
    /// <returns>Whether it is in there at all.</returns>
    public bool Find(string query, bool forward = true, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(query))
            return false;

        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        UITextPosition from = forward ? SelectionEnd : SelectionStart;

        for (int step = 0; step <= lines.Count; step++)
        {
            int line = forward ? (from.Line + step) % lines.Count : ((from.Line - step) % lines.Count + lines.Count) % lines.Count;
            string text = lines[line];
            int at;

            if (forward)
            {
                int start = step == 0 ? from.Column : 0;
                at = start <= text.Length ? text.IndexOf(query, start, comparison) : -1;
            }
            else
            {
                int before = step == 0 ? from.Column - 1 : text.Length - 1;
                at = before >= 0 ? text.LastIndexOf(query, Math.Min(before, text.Length - 1), comparison) : -1;
                if (at >= 0 && step == 0 && at + query.Length > from.Column) at = before - 1 >= 0 ? text.LastIndexOf(query, before - 1, comparison) : -1;
            }

            if (at >= 0)
            {
                Select(new UITextPosition(line, at), new UITextPosition(line, at + query.Length));
                return true;
            }
        }

        return false;
    }

    /// <summary>Every place a piece of text is found, for showing them all.</summary>
    public void FindAll(string query, List<UITextPosition> into, bool matchCase = false)
    {
        into.Clear();
        if (string.IsNullOrEmpty(query))
            return;

        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int line = 0; line < lines.Count; line++)
        {
            for (int at = lines[line].IndexOf(query, comparison); at >= 0; at = lines[line].IndexOf(query, at + query.Length, comparison))
                into.Add(new UITextPosition(line, at));
        }
    }

    /// <summary>Every place a piece of text is found swapped for another, as one step.</summary>
    /// <returns>How many were.</returns>
    public int ReplaceAll(string query, string with, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(query))
            return 0;

        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int count = 0;
        Record(EditKind.Other);

        for (int line = 0; line < lines.Count; line++)
        {
            string text = lines[line];
            int at = text.IndexOf(query, comparison);
            if (at < 0) continue;

            var built = new StringBuilder();
            int from = 0;
            for (; at >= 0; at = text.IndexOf(query, from, comparison))
            {
                built.Append(text, from, at - from).Append(with);
                from = at + query.Length;
                count++;
            }

            built.Append(text, from, text.Length - from);
            lines[line] = built.ToString();
        }

        if (count == 0)
        {
            undo.RemoveAt(undo.Count - 1);
            return 0;
        }

        Caret = Anchor = Clamp(Caret);
        Touched();
        return count;
    }

    /* The history */

    public void Undo()
    {
        if (undo.Count == 0)
            return;

        redo.Add(Current());
        Restore(undo[^1]);
        undo.RemoveAt(undo.Count - 1);
    }

    public void Redo()
    {
        if (redo.Count == 0)
            return;

        undo.Add(Current());
        Restore(redo[^1]);
        redo.RemoveAt(redo.Count - 1);
    }

    private Snapshot Current() => new(Text, Caret, Anchor);

    private void Restore(Snapshot snapshot)
    {
        SetLines(snapshot.Text);
        Caret = Clamp(snapshot.Caret);
        Anchor = Clamp(snapshot.Anchor);
        preferredColumn = Caret.Column;
        lastKind = EditKind.Other;
        Touched();
    }

    /// <summary>
    /// Helper method to note how the text is before a change, unless the change is more of the same as the one before
    /// it (a run of typing, a run of deleting), which goes back as one step.
    /// </summary>
    private void Record(EditKind kind)
    {
        // Whatever comes next changes the lines
        Snapshot before = kind != EditKind.Other && kind == lastKind && undo.Count > 0 ? default : Current();
        joined = null;

        if (before.Text is null)
            return;

        undo.Add(before);
        if (undo.Count > MAX_UNDO)
            undo.RemoveAt(0);

        redo.Clear();
        lastKind = kind;
    }

    /* The raw edits, no history and no caret, the caret is whoever called's */

    private UITextPosition InsertRaw(UITextPosition at, string text)
    {
        at = Clamp(at);
        string line = lines[at.Line];
        string before = line[..at.Column], after = line[at.Column..];

        int newline = text.IndexOf('\n');
        if (newline < 0)
        {
            lines[at.Line] = before + text + after;
            return at with { Column = at.Column + text.Length };
        }

        string[] pieces = text.Split('\n');
        lines[at.Line] = before + pieces[0];
        for (int i = 1; i < pieces.Length; i++)
            lines.Insert(at.Line + i, pieces[i]);

        int last = at.Line + pieces.Length - 1;
        int column = lines[last].Length;
        lines[last] += after;
        return new UITextPosition(last, column);
    }

    private void DeleteRaw(UITextPosition from, UITextPosition to)
    {
        if (from > to) (from, to) = (to, from);
        from = Clamp(from);
        to = Clamp(to);

        if (from.Line == to.Line)
        {
            lines[from.Line] = lines[from.Line].Remove(from.Column, to.Column - from.Column);
            return;
        }

        lines[from.Line] = lines[from.Line][..from.Column] + lines[to.Line][to.Column..];
        lines.RemoveRange(from.Line + 1, to.Line - from.Line);
    }

    private bool DeleteSelectionRaw()
    {
        if (!HasSelection)
            return false;

        UITextPosition start = SelectionStart;
        DeleteRaw(start, SelectionEnd);
        Caret = Anchor = start;
        return true;
    }

    private void Touched()
    {
        joined = null;
        Version++;
        Changed?.Invoke();
    }
}
