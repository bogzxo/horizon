using System.Text;
using System.Text.RegularExpressions;

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

/// <summary>How something is looked for, whether the case has to match, only whole words, and whether it's a regex (matched a line at a time).</summary>
public readonly record struct UIFindOptions(bool MatchCase = false, bool WholeWord = false, bool Regex = false);

/// <summary>A place something was found, which line, where on it and how long, a regex's matches being as long as they come out.</summary>
public readonly record struct UITextMatch(int Line, int Column, int Length);

/// <summary>A caret and the selection it drags along, the anchor being where the selection started.</summary>
public readonly record struct UITextSelection(UITextPosition Caret, UITextPosition Anchor)
{
    public UITextPosition Start => Caret < Anchor ? Caret : Anchor;
    public UITextPosition End => Caret < Anchor ? Anchor : Caret;
    public bool IsEmpty => Caret == Anchor;
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
/// <para>
/// It can have more than one caret (Sublime's, Lite XL's, VS Code's). <see cref="Caret"/> and <see cref="Anchor"/> are the
/// one put down last, the others are <see cref="ExtraCarets"/>, and typing, deleting, the moves and the edits of lines go
/// on at every one of them as one step to undo. Whatever puts the caret somewhere on its own (a click, a find, a
/// selection made by a program) lets go of the others.
/// </para>
/// </summary>
public sealed class UITextDocument
{
    // What one step of the undo history comes back to. The lines and not the text joined up, a line is a string nobody
    // changes, so a step shares every line it has with the text and the steps either side of it and costs a reference a
    // line. The joined text was 120 KB a step in a 2000 line file, three hundred of them kept
    private readonly record struct Snapshot(string[] Lines, UITextPosition Caret, UITextPosition Anchor);

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

    // The carets past the first, each with its selection, and whether an edit or a move is going round all of them, when
    // the edits inside it neither note a step of their own nor let go of the others
    private readonly List<UITextSelection> extras = [];
    private bool batching;

    /// <summary>The carets past the one put down last, each with its selection. Empty most of the time.</summary>
    public IReadOnlyList<UITextSelection> ExtraCarets => extras;

    /// <summary>Whether there is more than one caret.</summary>
    public bool HasExtraCarets => extras.Count > 0;

    /// <summary>How many carets there are, one at least.</summary>
    public int CaretCount => extras.Count + 1;

    /// <summary>Every caret but the one put down last let go of, escape.</summary>
    public void CollapseCarets() => extras.Clear();

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

    /// <summary>
    /// Called as lines are put in or taken out, with where (the place lines were put in at, or the end of what was taken
    /// out) and how many, more for put in and less for taken out. For what's kept by its line to move along with them, a
    /// code editor's diagnostics until they're said again.
    /// </summary>
    public event Action<UITextPosition, int>? LinesMoved;

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
        if (!batching)
            extras.Clear();

        Caret = Clamp(position);
        if (!selecting)
            Anchor = Caret;

        preferredColumn = Caret.Column;
        lastKind = EditKind.Other;
    }

    /// <summary>Selects from one place to another, the caret at the second.</summary>
    public void Select(UITextPosition from, UITextPosition to)
    {
        if (!batching)
            extras.Clear();

        Anchor = Clamp(from);
        Caret = Clamp(to);
        preferredColumn = Caret.Column;
        lastKind = EditKind.Other;
    }

    public void SelectAll() => Select(default, new UITextPosition(lines.Count - 1, lines[^1].Length));

    /// <summary>A character to the left or the right, over the end of a line onto the next. Without shift a selection is left at the end the arrow points at.</summary>
    public void MoveHorizontally(int direction, bool selecting)
    {
        if (AtEveryCaret(() => MoveHorizontally(direction, selecting), null))
            return;

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
        if (AtEveryCaret(() => MoveVertically(by, selecting), null))
            return;
        if (!batching)
            extras.Clear();

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
    public void MoveWord(int direction, bool selecting)
    {
        if (!AtEveryCaret(() => MoveWord(direction, selecting), null))
            MoveTo(WordFrom(Caret, direction), selecting);
    }

    /// <summary>The first thing on the line that isn't a space, or the start of the line if the caret is there already.</summary>
    public void MoveHome(bool selecting)
    {
        if (AtEveryCaret(() => MoveHome(selecting), null))
            return;

        string line = lines[Caret.Line];
        int first = line.Length - line.TrimStart().Length;
        MoveTo(new UITextPosition(Caret.Line, Caret.Column == first ? 0 : first), selecting);
    }

    public void MoveEnd(bool selecting)
    {
        if (!AtEveryCaret(() => MoveEnd(selecting), null))
            MoveTo(new UITextPosition(Caret.Line, lines[Caret.Line].Length), selecting);
    }

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
        if (AtEveryCaret(() => Type(character), EditKind.Typing))
            return;

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
        if (InsertAtEveryCaret(text))
            return;

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
        if (AtEveryCaret(NewLine, EditKind.Other))
            return;

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
        if (AtEveryCaret(Backspace, EditKind.Deleting))
            return;

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
        if (AtEveryCaret(Delete, EditKind.Deleting))
            return;

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
        if (AtEveryCaret(() => DeleteWord(direction), EditKind.Other))
            return;

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
        if (AtEveryCaret(IndentLines, EditKind.Other))
            return;

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
        if (AtEveryCaret(Outdent, EditKind.Other))
            return;

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

    /// <summary>The selection once more after itself, or the caret's line once more under itself.</summary>
    public void Duplicate()
    {
        if (AtEveryCaret(Duplicate, EditKind.Other))
            return;

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
        // Lines are moved for the one caret, the others let go of
        extras.Clear();
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

    // The pattern of the last regex looked for, made once for as long as the query and the options stay the same
    private (string Query, UIFindOptions Options) patternFor = (string.Empty, default);
    private Regex? pattern;
    private string? patternError;

    // Where the matches on a line are while one line is looked through, kept rather than made every time
    private readonly List<(int Column, int Length)> lineMatches = [];

    /// <summary>What was wrong with the last regex looked for, null when it was fine (or wasn't a regex).</summary>
    public string? FindError => patternError;

    /// <summary>
    /// Selects the next place a piece of text is found after the caret (or before it, going back), round past the end.
    /// </summary>
    /// <returns>Whether it is in there at all.</returns>
    public bool Find(string query, bool forward = true, bool matchCase = false) => Find(query, new UIFindOptions(MatchCase: matchCase), forward);

    /// <summary>
    /// Selects the next place something is found after the selection (or before it, going back), round past the end, as
    /// the options say (the case, whole words, a regex, which is matched a line at a time).
    /// </summary>
    /// <returns>Whether it is in there at all.</returns>
    public bool Find(string query, UIFindOptions options, bool forward = true) =>
        FindFrom(query, options, forward ? SelectionEnd : SelectionStart, forward);

    /// <summary>
    /// Selects the first place something is found from a place on (or the last before it, going back), round past the
    /// end. The find bar looks from where it was opened as its query is typed, so the match grows rather than hops on.
    /// </summary>
    public bool FindFrom(string query, UIFindOptions options, UITextPosition from, bool forward = true)
    {
        if (string.IsNullOrEmpty(query) || !Prepare(query, options))
            return false;

        from = Clamp(from);
        for (int step = 0; step <= lines.Count; step++)
        {
            int line = forward ? (from.Line + step) % lines.Count : ((from.Line - step) % lines.Count + lines.Count) % lines.Count;
            MatchLine(lines[line], query, options);

            int pick = -1;
            if (forward)
            {
                for (int i = 0; i < lineMatches.Count && pick < 0; i++)
                {
                    if (step > 0 || lineMatches[i].Column >= from.Column || (step == lines.Count && lineMatches[i].Column < from.Column))
                        pick = i;
                }
            }
            else
            {
                for (int i = lineMatches.Count - 1; i >= 0 && pick < 0; i--)
                {
                    if (step > 0 || lineMatches[i].Column + lineMatches[i].Length <= from.Column)
                        pick = i;
                }
            }

            if (pick >= 0)
            {
                var (column, length) = lineMatches[pick];
                Select(new UITextPosition(line, column), new UITextPosition(line, column + length));
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

        var options = new UIFindOptions(MatchCase: matchCase);
        if (!Prepare(query, options))
            return;

        for (int line = 0; line < lines.Count; line++)
        {
            MatchLine(lines[line], query, options);
            foreach (var (column, _) in lineMatches)
                into.Add(new UITextPosition(line, column));
        }
    }

    /// <summary>Every place something is found as the options say, where and how long, for showing them all and counting them.</summary>
    public void FindAll(string query, UIFindOptions options, List<UITextMatch> into)
    {
        into.Clear();
        if (string.IsNullOrEmpty(query) || !Prepare(query, options))
            return;

        for (int line = 0; line < lines.Count; line++)
        {
            MatchLine(lines[line], query, options);
            foreach (var (column, length) in lineMatches)
                into.Add(new UITextMatch(line, column, length));
        }
    }

    /// <summary>Every place a piece of text is found swapped for another, as one step.</summary>
    /// <returns>How many were.</returns>
    public int ReplaceAll(string query, string with, bool matchCase = false) => ReplaceAll(query, with, new UIFindOptions(MatchCase: matchCase));

    /// <summary>
    /// Every place something is found swapped for another, as one step. With a regex, what it's swapped for can name the
    /// regex's groups, $1 and ${name}.
    /// </summary>
    /// <returns>How many were.</returns>
    public int ReplaceAll(string query, string with, UIFindOptions options)
    {
        if (string.IsNullOrEmpty(query) || !Prepare(query, options))
            return 0;

        int count = 0;
        Record(EditKind.Other);

        for (int line = 0; line < lines.Count; line++)
        {
            string text = lines[line];
            MatchLine(text, query, options);
            if (lineMatches.Count == 0) continue;

            var built = new StringBuilder();
            int from = 0;
            foreach (var (column, length) in lineMatches)
            {
                built.Append(text, from, column - from).Append(Replacement(text, column, length, with, options));
                from = column + length;
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

    /// <summary>
    /// The match that's selected swapped for another and the next one selected, one step to undo. Nothing selected that
    /// matches, the next match is only selected, the way every editor's replace does it.
    /// </summary>
    /// <returns>Whether one was swapped.</returns>
    public bool ReplaceNext(string query, string with, UIFindOptions options)
    {
        if (string.IsNullOrEmpty(query) || !Prepare(query, options))
            return false;

        bool replaced = false;
        if (HasSelection && SelectionStart.Line == SelectionEnd.Line)
        {
            UITextPosition start = SelectionStart, end = SelectionEnd;
            string text = lines[start.Line];
            MatchLine(text, query, options);
            foreach (var (column, length) in lineMatches)
            {
                if (column != start.Column || column + length != end.Column)
                    continue;

                string swapped = Replacement(text, column, length, with, options);
                Record(EditKind.Other);
                DeleteRaw(start, end);
                UITextPosition after = InsertRaw(start, swapped);
                MoveTo(after);
                Touched();
                replaced = true;
                break;
            }
        }

        Find(query, options);
        return replaced;
    }

    // What a match is swapped for, a regex's groups put in
    private string Replacement(string line, int column, int length, string with, UIFindOptions options)
    {
        if (!options.Regex || pattern is null)
            return with;

        Match match = pattern.Match(line, column, length);
        return match.Success && match.Index == column ? match.Result(with) : with;
    }

    // The regex made for a query, if it is one. False for a regex that doesn't build, which finds nothing and says why
    private bool Prepare(string query, UIFindOptions options)
    {
        if (!options.Regex)
        {
            patternError = null;
            return true;
        }

        if (pattern is not null && patternFor == (query, options))
            return true;

        patternFor = (query, options);
        try
        {
            pattern = new Regex(query, (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            patternError = null;
            return true;
        }
        catch (ArgumentException e)
        {
            pattern = null;
            patternError = e.Message;
            return false;
        }
    }

    // Every match on a line into lineMatches, left to right and not overlapping
    private void MatchLine(string text, string query, UIFindOptions options)
    {
        lineMatches.Clear();

        if (options.Regex)
        {
            if (pattern is null)
                return;

            try
            {
                for (Match match = pattern.Match(text); match.Success; match = match.NextMatch())
                {
                    // A match of nothing (^, a*) would be found forever, it isn't a place anybody wants to go
                    if (match.Length == 0) continue;
                    if (options.WholeWord && !IsWholeWord(text, match.Index, match.Length)) continue;
                    lineMatches.Add((match.Index, match.Length));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // a pattern that takes that long on one line finds nothing on it
            }

            return;
        }

        StringComparison comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int at = text.IndexOf(query, comparison); at >= 0; at = text.IndexOf(query, at + query.Length, comparison))
        {
            if (!options.WholeWord || IsWholeWord(text, at, query.Length))
                lineMatches.Add((at, query.Length));
        }
    }

    private static bool IsWholeWord(string text, int at, int length) =>
        (at == 0 || !IsWordChar(text[at - 1])) && (at + length >= text.Length || !IsWordChar(text[at + length]));

    /* The history */

    public void Undo()
    {
        extras.Clear();
        if (undo.Count == 0)
            return;

        redo.Add(Current());
        Restore(undo[^1]);
        undo.RemoveAt(undo.Count - 1);
    }

    public void Redo()
    {
        extras.Clear();
        if (redo.Count == 0)
            return;

        undo.Add(Current());
        Restore(redo[^1]);
        redo.RemoveAt(redo.Count - 1);
    }

    private Snapshot Current() => new([.. lines], Caret, Anchor);

    private void Restore(Snapshot snapshot)
    {
        joined = null;
        lines.Clear();
        lines.AddRange(snapshot.Lines);
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
        // Going round every caret, the step was noted once before the first of them
        if (batching)
        {
            joined = null;
            return;
        }

        // Whatever comes next changes the lines
        Snapshot before = kind != EditKind.Other && kind == lastKind && undo.Count > 0 ? default : Current();
        joined = null;

        if (before.Lines is null)
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
        LinesMoved?.Invoke(at, pieces.Length - 1);
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
        LinesMoved?.Invoke(to, from.Line - to.Line);
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
        if (batching)
            return;

        Version++;
        Changed?.Invoke();
    }

    /* More than one caret */

    // Done at every caret when there's more than one, true when it was and the caller has nothing more to do. An edit
    // (with a kind) is one step to undo for all of them
    private bool AtEveryCaret(Action single, EditKind? edit)
    {
        if (extras.Count == 0 || batching)
            return false;

        if (edit is { } kind)
            EditAtEvery(kind, single);
        else
            MoveAtEvery(single);
        return true;
    }

    // Every caret, the one put down last first in the list
    private List<(UITextSelection Selection, bool Primary)> AllCarets()
    {
        var all = new List<(UITextSelection, bool)>(extras.Count + 1) { (new UITextSelection(Caret, Anchor), true) };
        foreach (UITextSelection extra in extras)
            all.Add((extra, false));
        return all;
    }

    private void MoveAtEvery(Action single)
    {
        // Up and down keep to the column the caret put down last was going for, the others to their own
        int keep = preferredColumn, primaryKeeps = keep;
        var moved = new List<(UITextSelection, bool)>();
        batching = true;
        try
        {
            foreach (var (selection, primary) in AllCarets())
            {
                Caret = selection.Caret;
                Anchor = selection.Anchor;
                preferredColumn = primary ? keep : selection.Caret.Column;
                single();
                if (primary)
                    primaryKeeps = preferredColumn;
                moved.Add((new UITextSelection(Caret, Anchor), primary));
            }
        }
        finally
        {
            batching = false;
        }

        SetCarets(moved);
        preferredColumn = primaryKeeps;
        lastKind = EditKind.Other;
    }

    // An edit at every caret, from the last in the text to the first, so an edit never moves the carets still to come.
    // Where the ones already done end up is kept as offsets into the text and moved along by however much each edit after
    // made the text longer or shorter, every edit happening at or before the carets it comes after
    private void EditAtEvery(EditKind kind, Action single)
    {
        Record(kind);

        var all = AllCarets();
        all.Sort((a, b) => b.Selection.Start.CompareTo(a.Selection.Start));

        var done = new List<(int Caret, int Anchor, bool Primary)>(all.Count);
        int length = TotalLength();
        batching = true;
        try
        {
            foreach (var (selection, primary) in all)
            {
                Caret = Clamp(selection.Caret);
                Anchor = Clamp(selection.Anchor);
                preferredColumn = Caret.Column;
                single();

                int now = TotalLength(), delta = now - length;
                length = now;
                for (int i = 0; i < done.Count; i++)
                    done[i] = (done[i].Caret + delta, done[i].Anchor + delta, done[i].Primary);
                done.Add((OffsetOf(Caret), OffsetOf(Anchor), primary));
            }
        }
        finally
        {
            batching = false;
        }

        var placed = new List<(UITextSelection, bool)>(done.Count);
        foreach (var (caret, anchor, primary) in done)
            placed.Add((new UITextSelection(PositionOf(caret), PositionOf(anchor)), primary));

        SetCarets(placed);
        preferredColumn = Caret.Column;
        lastKind = kind;
        Touched();
    }

    // What's pasted at every caret, a line each when there are as many lines as carets (copied from that many), the
    // whole of it at each otherwise. True when there was more than one caret to do it at
    private bool InsertAtEveryCaret(string text)
    {
        if (extras.Count == 0 || batching)
            return false;

        string[] pieces = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (pieces.Length != extras.Count + 1)
        {
            EditAtEvery(EditKind.Other, () => Insert(text));
            return true;
        }

        // The carets are gone through from the last in the text, so the pieces are handed out from the last too
        int next = pieces.Length - 1;
        EditAtEvery(EditKind.Other, () => Insert(pieces[Math.Max(0, next--)]));
        return true;
    }

    // The carets put down again, the one marked as put down last the first, and any that run into each other made one
    private void SetCarets(List<(UITextSelection Selection, bool Primary)> carets)
    {
        carets.Sort((a, b) => a.Selection.Start.CompareTo(b.Selection.Start));
        var merged = new List<(UITextSelection Selection, bool Primary)>(carets.Count);
        foreach (var (selection, primary) in carets)
        {
            if (merged.Count > 0)
            {
                var (last, wasPrimary) = merged[^1];
                bool touching = selection.Start < last.End || selection.Start == last.Start;
                if (touching)
                {
                    UITextPosition end = selection.End > last.End ? selection.End : last.End;
                    // the two are one selection running the way the first did
                    var joinedSelection = last.Caret >= last.Anchor ? new UITextSelection(end, last.Start) : new UITextSelection(last.Start, end);
                    merged[^1] = (last.IsEmpty && selection.IsEmpty ? last : joinedSelection, wasPrimary || primary);
                    continue;
                }
            }

            merged.Add((selection, primary));
        }

        extras.Clear();
        bool primarySet = false;
        foreach (var (selection, primary) in merged)
        {
            if (primary && !primarySet)
            {
                Caret = Clamp(selection.Caret);
                Anchor = Clamp(selection.Anchor);
                primarySet = true;
            }
            else
            {
                extras.Add(new UITextSelection(Clamp(selection.Caret), Clamp(selection.Anchor)));
            }
        }
    }

    private int TotalLength()
    {
        int length = lines.Count - 1;
        foreach (string line in lines)
            length += line.Length;
        return length;
    }

    private int OffsetOf(UITextPosition at)
    {
        at = Clamp(at);
        int offset = 0;
        for (int line = 0; line < at.Line; line++)
            offset += lines[line].Length + 1;
        return offset + at.Column;
    }

    private UITextPosition PositionOf(int offset)
    {
        for (int line = 0; line < lines.Count; line++)
        {
            if (offset <= lines[line].Length)
                return new UITextPosition(line, Math.Max(0, offset));
            offset -= lines[line].Length + 1;
        }

        return new UITextPosition(lines.Count - 1, lines[^1].Length);
    }

    /// <summary>
    /// Control with D. Nothing selected, the word at the caret is. Something selected (on one line), the next place it is
    /// after the last of the carets gets a caret of its own with it selected, round past the end. False when there's no
    /// other place it is.
    /// </summary>
    public bool AddNextOccurrence()
    {
        if (!HasSelection)
        {
            var (start, end) = WordAt(Caret);
            if (end <= start)
                return false;

            extras.Clear();
            Anchor = new UITextPosition(Caret.Line, start);
            Caret = new UITextPosition(Caret.Line, end);
            preferredColumn = Caret.Column;
            return true;
        }

        if (SelectionStart.Line != SelectionEnd.Line)
            return false;

        string query = SelectedText;
        UITextPosition from = SelectionEnd;
        foreach (UITextSelection extra in extras)
            from = extra.End > from ? extra.End : from;

        for (int step = 0; step <= lines.Count; step++)
        {
            int line = (from.Line + step) % lines.Count;
            string text = lines[line];
            for (int at = text.IndexOf(query, step == 0 ? Math.Min(from.Column, text.Length) : 0, StringComparison.Ordinal); at >= 0; at = text.IndexOf(query, at + 1, StringComparison.Ordinal))
            {
                var start = new UITextPosition(line, at);
                if (IsCaretAt(start))
                    continue;

                extras.Add(new UITextSelection(Caret, Anchor));
                Anchor = start;
                Caret = new UITextPosition(line, at + query.Length);
                preferredColumn = Caret.Column;
                return true;
            }
        }

        return false;
    }

    // Whether one of the carets' selections starts at a place
    private bool IsCaretAt(UITextPosition start)
    {
        if (SelectionStart == start)
            return true;
        foreach (UITextSelection extra in extras)
        {
            if (extra.Start == start)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Alt and a click, a caret put down there as well, the one put down last. A click on a caret that's there already
    /// takes it away, as long as it isn't the only one.
    /// </summary>
    public void AddCaret(UITextPosition at)
    {
        at = Clamp(at);
        int there = extras.FindIndex(extra => extra.IsEmpty && extra.Caret == at);
        if (there >= 0)
        {
            extras.RemoveAt(there);
            return;
        }

        if (!HasSelection && Caret == at)
        {
            if (extras.Count == 0)
                return;

            UITextSelection last = extras[^1];
            extras.RemoveAt(extras.Count - 1);
            Caret = last.Caret;
            Anchor = last.Anchor;
            return;
        }

        extras.Add(new UITextSelection(Caret, Anchor));
        Caret = Anchor = at;
        preferredColumn = at.Column;
    }

    /// <summary>Control, alt and up or down, a caret on the line over the highest of them (or under the lowest), at the same column.</summary>
    public void AddCaretVertically(int direction)
    {
        UITextPosition edge = Caret;
        foreach (UITextSelection extra in extras)
        {
            if (direction < 0 ? extra.Caret.Line < edge.Line : extra.Caret.Line > edge.Line)
                edge = extra.Caret;
        }

        int line = edge.Line + direction;
        if (line < 0 || line >= lines.Count)
            return;

        extras.Add(new UITextSelection(Caret, Anchor));
        Caret = Anchor = new UITextPosition(line, Math.Min(preferredColumn, lines[line].Length));
    }

    /// <summary>Control, shift and L, the selection split into a caret at the end of every line it runs over (Sublime's).</summary>
    public void CaretsAtLineEnds()
    {
        if (!HasSelection)
            return;

        var (first, last) = SelectedLines();
        extras.Clear();
        for (int line = first; line < last; line++)
        {
            var end = new UITextPosition(line, lines[line].Length);
            extras.Add(new UITextSelection(end, end));
        }

        Caret = Anchor = new UITextPosition(last, lines[last].Length);
        preferredColumn = Caret.Column;
    }

    /// <summary>
    /// Alt, shift and a drag, a column, a caret on every line between two places selecting from the one's column to the
    /// other's (short lines as far as they go).
    /// </summary>
    public void SelectColumn(UITextPosition from, UITextPosition to)
    {
        from = Clamp(from);
        to = Clamp(to);
        extras.Clear();

        int step = to.Line >= from.Line ? 1 : -1;
        for (int line = from.Line; line != to.Line; line += step)
        {
            int length = lines[line].Length;
            extras.Add(new UITextSelection(new UITextPosition(line, Math.Min(to.Column, length)), new UITextPosition(line, Math.Min(from.Column, length))));
        }

        int last = lines[to.Line].Length;
        Anchor = new UITextPosition(to.Line, Math.Min(from.Column, last));
        Caret = new UITextPosition(to.Line, Math.Min(to.Column, last));
        preferredColumn = to.Column;
    }

    /// <summary>What every caret has selected, the first in the text first, a line each, for copying the lot.</summary>
    public string SelectedTextOfEvery()
    {
        if (extras.Count == 0)
            return SelectedText;

        var all = AllCarets();
        all.Sort((a, b) => a.Selection.Start.CompareTo(b.Selection.Start));
        var built = new StringBuilder();
        foreach (var (selection, _) in all)
        {
            if (built.Length > 0) built.Append('\n');
            built.Append(TextBetween(selection.Start, selection.End));
        }
        return built.ToString();
    }
}
