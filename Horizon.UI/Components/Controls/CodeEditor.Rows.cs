using System.Numerics;

using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The rows the code is drawn in. A line was a row once, then a long line could be wrapped over several (WordWrap) and an
// object folded away into the one line that opens it, so everything that drew or hit a line goes by rows now. A row is a
// piece of a line, from a column to a column, with how far in it starts (a wrapped line's rows after the first start as
// far in as the line does). Folding goes by brackets, a line that opens one closed further down can fold, and the lines
// the top of the view is inside of (moves: { then uppercut: { then phases: {) are held at the top, VS Code's sticky scroll.
public partial class CodeEditor
{
    // How many lines are held at the top at the most
    private const int MAX_STICKY = 4;

    /// <summary>
    /// A row the code is drawn in, a piece of a line from one column to another, so far in from the left. A lens row has
    /// none of the line on it, it's the room over the line its lenses are written in (CodeEditor.LensesAbove).
    /// </summary>
    private readonly record struct CodeRow(int Line, int Start, int End, float Indent, bool Lens = false);

    private readonly List<CodeRow> rows = [];

    // The first row of every line, and for a line folded away the row of the line it's folded into
    private int[] lineRow = [0];

    // What the rows were made for, made again when any of it changes. The shapes the code wraps round (and the scroll
    // they were last flowed at to the pixel, they stay put on screen as the code goes by) count only while there are any
    private (int Version, int Folds, bool Wrap, float Width, float Scale, int Shapes, int Scrolled, int Inlays, int Lenses) rowsFor = (-1, -1, false, -1.0f, -1.0f, 0, 0, -1, -1);

    // The fewest columns a row is cut down to by a shape, with less room than that beside one it runs over it instead
    private const int MIN_SHAPED_COLUMNS = 8;

    // The shapes the rows are being made round, where they are on screen, and where the rows are as they're made
    private readonly List<UIWrapArea> shapesNow = [];
    private float shapedTop, shapedLeft, shapedLineHeight;

    // Where every bracket that opens on a line and closes on a later one closes, by the line it opens on, as the last
    // colouring found them. The lines that are folded, by the line they open on, and a count of the changes to them
    private readonly Dictionary<int, int> foldable = [];
    private readonly HashSet<int> folded = [];
    private int foldsChanged;

    // What the lines and the caret were last tick, to move the folds along with lines put in or taken out above them
    private int linesWere = 1, caretLineWas;

    // The lines held at the top, and what they were worked out for
    private readonly List<int> sticky = [];
    private (int Line, int Coloured, int Folds) stickyFor = (-1, -1, -1);

    /// <summary>Whether long lines are wrapped at the edge (Alt+Z in Hex), off they run off it and the view scrolls along.</summary>
    public bool WordWrap
    {
        get => wordWrap;
        set
        {
            wordWrap = value;
            if (value)
                scrollX = 0.0f;
        }
    }

    private bool wordWrap;

    /// <summary>Whether the lines the top of the view is inside of are held at the top as it scrolls.</summary>
    public bool StickyScroll { get; set; } = true;

    /// <summary>How many rows the code takes as it's drawn, more than its lines when they wrap, fewer when some are folded.</summary>
    public int RowCount => rows.Count;

    /// <summary>How many of the rows are the room over a line for its lenses (CodeEditor.LensesAbove) rather than any of the code.</summary>
    public int LensRowCount => lensRows;

    private int lensRows;

    /// <summary>The lines that are folded, by the line each opens on.</summary>
    public IReadOnlyCollection<int> FoldedLines => folded;

    /// <summary>Whether a line opens a bracket that closes further down, and so can be folded.</summary>
    public bool CanFold(int line) => foldable.ContainsKey(line);

    /// <summary>A line folded or opened again, a line that can't be folded is left alone.</summary>
    public void ToggleFold(int line)
    {
        if (folded.Remove(line) || (foldable.ContainsKey(line) && folded.Add(line)))
            foldsChanged++;
    }

    /// <summary>Control, shift and [, the innermost thing the caret is inside of that can fold is folded.</summary>
    public void FoldAtCaret()
    {
        int line = document.Caret.Line, best = -1;
        foreach (var (start, end) in foldable)
        {
            if (start <= line && line <= end && start > best && !folded.Contains(start))
                best = start;
        }

        if (best >= 0)
        {
            folded.Add(best);
            foldsChanged++;
            document.MoveTo(new UITextPosition(best, document.LineAt(best).Length));
        }
    }

    /// <summary>Control, shift and ], what's folded on the caret's line or round it opened again.</summary>
    public void UnfoldAtCaret()
    {
        int line = document.Caret.Line;
        int before = folded.Count;
        folded.RemoveWhere(start => start == line || (foldable.TryGetValue(start, out int end) && start < line && line <= end));
        if (folded.Count != before)
            foldsChanged++;
    }

    /// <summary>
    /// Every bracket that opens on a line and closes further down folded, as deep as a level and deeper, 1 folding everything
    /// there is and 2 leaving the outermost open (the keys of a file's top object, its moves, folded each).
    /// </summary>
    public void FoldAll(int level = 1)
    {
        foreach (int start in foldable.Keys)
        {
            if (DepthAt(start) >= level - 1)
                folded.Add(start);
        }

        foldsChanged++;
    }

    /// <summary>Everything folded opened again.</summary>
    public void UnfoldAll()
    {
        folded.Clear();
        foldsChanged++;
    }

    // How deep in brackets a line starts, as the last colouring found it
    private int DepthAt(int line) => line >= 0 && line < lineStarts.Length ? lineStarts[line].Depth : 0;

    /// <summary>
    /// Where the brackets open and close worked out again out of the colouring that was just taken in, and the folds that
    /// don't open anything any more let go of.
    /// </summary>
    private void FindFoldable()
    {
        foldable.Clear();
        var open = new Stack<int>();
        for (int line = 0; line < colouredLines.Length && line < spans.Length; line++)
        {
            string text = colouredLines[line];
            foreach (SyntaxSpan span in spans[line])
            {
                if (span.Kind is not (SyntaxKind.Bracket1 or SyntaxKind.Bracket2 or SyntaxKind.Bracket3) || span.Start >= text.Length)
                    continue;

                char character = text[span.Start];
                if (character is '{' or '[' or '(')
                {
                    open.Push(line);
                }
                else if (open.Count > 0)
                {
                    int from = open.Pop();
                    if (from < line && (!foldable.TryGetValue(from, out int was) || was < line))
                        foldable[from] = line;
                }
            }
        }

        if (folded.RemoveWhere(start => !foldable.ContainsKey(start)) > 0)
            foldsChanged++;
    }

    // Every tick, the folds moved along with lines put in or taken out above them, and opened when the caret goes inside
    private void UpdateFolds()
    {
        int lines = document.LineCount, caret = document.Caret.Line;
        if (lines != linesWere && folded.Count > 0)
        {
            int by = lines - linesWere, pivot = Math.Min(caret, caretLineWas);
            var moved = new List<int>(folded.Count);
            foreach (int start in folded)
                moved.Add(start > pivot ? start + by : start);

            folded.Clear();
            foreach (int start in moved)
            {
                if (start >= 0 && start < lines)
                    folded.Add(start);
            }

            foldsChanged++;
        }

        linesWere = lines;
        caretLineWas = caret;

        // A caret that went inside something folded opens it, a caret is never hidden
        if (folded.Count > 0 && IsHidden(caret, out int into))
        {
            folded.Remove(into);
            foldsChanged++;
        }
    }

    // Whether a line is folded away, and into which line, the outermost fold round it
    private bool IsHidden(int line, out int into)
    {
        into = -1;
        foreach (int start in folded)
        {
            if (start < line && foldable.TryGetValue(start, out int end) && line <= end && (into < 0 || start < into))
                into = start;
        }

        return into >= 0;
    }

    /// <summary>The rows made again if anything they hang on changed (the text, the folds, the wrapping and the width it wraps at).</summary>
    private void EnsureRows(UISkin skin, float scale)
    {
        UIRect area = TextArea(skin, scale);
        float width = wordWrap ? MathF.Round(area.Width) : 0.0f;
        float lineHeight = FontOf(skin).LineHeight * scale;

        // Where the shapes are this time, and what of it the rows hang on
        shapesNow.Clear();
        if (wordWrap)
        {
            if (BackdropShape is { } picked && Backdrop is { IsValid: true } picture && BackdropOpacity > 0.0f)
                shapesNow.Add(new UIWrapArea(picked, BackdropBounds(area, picture), BackdropMargin));
            foreach (UIWrapArea placed in WrapAreas)
                shapesNow.Add(placed with { Bounds = new UIRect(placed.Bounds.Min + area.Min, placed.Bounds.Max + area.Min) });
        }

        int shapes = 0, scrolled = 0;
        if (shapesNow.Count > 0)
        {
            var hash = new HashCode();
            foreach (UIWrapArea placed in shapesNow)
            {
                hash.Add(placed.Shape);
                hash.Add(placed.Bounds);
                hash.Add(placed.Margin);
            }

            shapes = hash.ToHashCode() | 1;
            scrolled = (int)MathF.Round(scrollY);
        }

        var wanted = (document.Version, foldsChanged, wordWrap, width, scale, shapes, scrolled, wordWrap ? inlaysVersion : 0, lensRowsChanged);
        if (rowsFor == wanted && rows.Count > 0)
            return;

        // The top of row 0 as the rows are flowed, the scroll to the pixel (to the row it was a staircase, every line
        // flowed for where it was up to a line ago), and the left of the code
        shapedTop = area.Max.Y + scrolled;
        shapedLeft = area.Min.X + TEXT_PAD;
        shapedLineHeight = lineHeight;

        rowsFor = wanted;
        rows.Clear();
        int count = document.LineCount;
        if (lineRow.Length < count)
            lineRow = new int[count];

        float digit = FontOf(skin).Measure("0", scale, markup: false).X;
        int columns = wordWrap ? Math.Max(8, (int)((width - TEXT_PAD * 2.0f) / MathF.Max(1.0f, digit))) : int.MaxValue;

        int lens = 0;
        lensRows = 0;
        for (int line = 0; line < count; line++)
        {
            // A line with lenses has a row over it for them, the line itself starts on the row after
            if (lensesAbove.Count > 0 && LensAboveOn(line, ref lens))
            {
                rows.Add(new CodeRow(line, 0, 0, 0.0f, Lens: true));
                lensRows++;
            }

            lineRow[line] = rows.Count;
            string text = document.LineAt(line);
            AddRows(line, text, columns, digit);

            // A folded line's own rows, then every line it folds is drawn as it
            if (folded.Contains(line) && foldable.TryGetValue(line, out int end))
            {
                int into = rows.Count - 1;
                for (int hidden = line + 1; hidden <= end && hidden < count; hidden++)
                    lineRow[hidden] = into;
                line = Math.Min(end, count - 1);
            }
        }
    }

    // A line's rows, one when it isn't wrapped or fits, cut at the last space before the edge when it is and doesn't
    private void AddRows(int line, string text, int columns, float digit)
    {
        float at = 0.0f;
        if (!wordWrap || Columns(text, 0, text.Length) + InlayColumnsOf(line) <= RoomOn(rows.Count, 0.0f, columns, digit, out at))
        {
            rows.Add(new CodeRow(line, 0, text.Length, at));
            return;
        }

        // Rows after the first start as far in as the line does, up to half the width
        int indent = 0;
        while (indent < text.Length && text[indent] is ' ' or '\t') indent++;
        int indentColumns = Math.Min(Columns(text, 0, indent), columns / 2);

        int start = 0;
        bool firstRow = true;
        while (start < text.Length)
        {
            int room = RoomOn(rows.Count, firstRow ? 0.0f : indentColumns * digit, firstRow ? columns : columns - indentColumns, digit, out float rowIndent);
            int end = start, used = 0;
            // A hint just before a character takes its room along with it
            while (end < text.Length && used + (text[end] == '\t' ? TabSize : 1) + InlayColumnsAt(line, end) <= room)
            {
                used += (text[end] == '\t' ? TabSize : 1) + InlayColumnsAt(line, end);
                end++;
            }

            if (end < text.Length)
            {
                int space = text.LastIndexOf(' ', end - 1, end - start);
                if (space > start)
                    end = space + 1;
            }

            rows.Add(new CodeRow(line, start, Math.Max(end, start + 1), rowIndent));
            start = Math.Max(end, start + 1);
            firstRow = false;
        }
    }

    // How many columns the row about to be made has, and how far in it starts, with the shapes on its height taken out of
    // it. A shape with room before it cuts the row short there, one at the left of it has the row start after it, and one
    // that leaves too little room either way is written over, the way it was before there were shapes
    private int RoomOn(int row, float indent, int columns, float digit, out float at)
    {
        at = indent;
        if (shapesNow.Count == 0)
            return columns;

        float top = shapedTop - row * shapedLineHeight, bottom = top - shapedLineHeight;
        float from = shapedLeft + indent, to = from + columns * digit;

        foreach (UIWrapArea placed in shapesNow)
        {
            if (!placed.Shape.Across(placed.Bounds, bottom, top, out float shapeFrom, out float shapeTo, placed.Margin) || shapeTo <= from || shapeFrom >= to)
                continue;

            if (shapeFrom - from >= MIN_SHAPED_COLUMNS * digit)
                to = MathF.Min(to, shapeFrom);
            else
                from = MathF.Max(from, shapeTo);
        }

        int room = (int)((to - from) / MathF.Max(1.0f, digit));
        if (room < MIN_SHAPED_COLUMNS)
            return columns;

        at = from - shapedLeft;
        return Math.Min(room, columns);
    }

    // How many columns a piece of a line takes, a tab as so many spaces
    private int Columns(string text, int from, int to)
    {
        int columns = 0;
        for (int i = from; i < to; i++)
            columns += text[i] == '\t' ? TabSize : 1;
        return columns;
    }

    /// <summary>The row a place is on, the folded line's for a place folded away.</summary>
    private int RowOf(int line, int column)
    {
        if (rows.Count == 0)
            return 0;

        line = Math.Clamp(line, 0, document.LineCount - 1);
        int row = lineRow[Math.Min(line, lineRow.Length - 1)];
        if (row >= rows.Count)
            return rows.Count - 1;
        if (rows[row].Line != line)
            return row;

        // Along the line's rows to the one the column is on, the end of one row being the start of the next
        while (row + 1 < rows.Count && rows[row + 1].Line == line && column >= rows[row + 1].Start)
            row++;
        return row;
    }

    private float RowTop(UIRect area, int row, float lineHeight) => area.Max.Y - (row * lineHeight - scrollY);

    /// <summary>The top of the row a place is on.</summary>
    private float TopOf(UIRect area, int line, int column, float lineHeight) => RowTop(area, RowOf(line, column), lineHeight);

    /// <summary>The top of a line's first row, for what goes by the line (its number, its icon).</summary>
    private float LineTop(UIRect area, int line, float lineHeight) => RowTop(area, RowOf(line, 0), lineHeight);

    /// <summary>How far along its row a place is, from the left of the code.</summary>
    private float ColumnX(UISkin skin, float scale, int line, int column)
    {
        string text = document.LineAt(line);
        column = Math.Clamp(column, 0, text.Length);
        if (rows.Count == 0)
            return column <= 0 ? 0.0f : Width(skin, scale, text.AsSpan(0, column));

        int index = RowOf(line, column);
        CodeRow row = rows[index];
        if (row.Line != line)
            return 0.0f;

        int from = Math.Min(row.Start, column);
        return row.Indent + (column <= from ? 0.0f : Width(skin, scale, text.AsSpan(from, column - from)) + InlaysWidth(skin, scale, line, from, column));
    }

    /// <summary>How far along its last row the end of a line is, past any hints after it, for what goes after the line.</summary>
    private float EndX(UISkin skin, float scale, int line, int length) => ColumnX(skin, scale, line, length) + InlaysWidth(skin, scale, line, length, int.MaxValue);

    // The lines the top of the view is inside of, outermost first, worked out when the top or the folds change
    private List<int> StickyLines(int firstLine)
    {
        if (!StickyScroll)
        {
            sticky.Clear();
            return sticky;
        }

        var wanted = (firstLine, coloured, foldsChanged);
        if (stickyFor == wanted)
            return sticky;

        stickyFor = wanted;
        sticky.Clear();
        foreach (var (start, end) in foldable)
        {
            int header = HeaderOf(start);
            if (header < firstLine && firstLine <= end && !sticky.Contains(header))
                sticky.Add(header);
        }

        sticky.Sort();
        if (sticky.Count > MAX_STICKY)
            sticky.RemoveRange(0, sticky.Count - MAX_STICKY);
        return sticky;
    }

    // The line that says what a bracket opens, the bracket's own line unless it's on a line of its own (C# the Allman way,
    // public int Method() and then { under it), where it's the line before it with something on it that isn't a comment
    // or an attribute. The held lines were a column of lone {s
    private int HeaderOf(int start)
    {
        if (document.LineAt(start).AsSpan().Trim() is not ("{" or "[" or "("))
            return start;

        for (int line = start - 1; line >= 0 && line > start - 8; line--)
        {
            ReadOnlySpan<char> text = document.LineAt(line).AsSpan().Trim();
            if (text.Length == 0 || text.StartsWith("//") || (text[0] == '[' && text[^1] == ']'))
                continue;
            return line;
        }

        return start;
    }

    // Where the lines held at the top are, as many rows as there are of them from the top of the code
    private UIRect StickyBounds(UIRect area, float lineHeight, int count) =>
        new(new Vector2(area.Min.X, area.Max.Y - count * lineHeight), area.Max);

    /// <summary>A press on the lines held at the top goes to the one pressed, true when it was on them.</summary>
    private bool PressSticky(Vector2 point)
    {
        if (paintedSkin is not { } skin || sticky.Count == 0)
            return false;

        float lineHeight = FontOf(skin).LineHeight * paintedScale;
        UIRect area = TextArea(skin, paintedScale);
        if (!StickyBounds(area, lineHeight, sticky.Count).Contains(point))
            return false;

        int index = Math.Clamp((int)((area.Max.Y - point.Y) / lineHeight), 0, sticky.Count - 1);
        GoTo(sticky[index], document.LineAt(sticky[index]).Length - document.LineAt(sticky[index]).TrimStart().Length);
        return true;
    }

    /// <summary>A press on the chevron of a line in the gutter folds or opens it, true when it was on one.</summary>
    private bool PressFold(Vector2 point, UITextPosition at)
    {
        if (paintedSkin is not { } skin)
            return false;

        UIRect area = TextArea(skin, paintedScale);
        float digit = FontOf(skin).Measure("0", paintedScale, markup: false).X;
        if (point.X < area.Min.X - digit * GUTTER_GAP || point.X >= area.Min.X || !foldable.ContainsKey(at.Line))
            return false;

        ToggleFold(at.Line);
        return true;
    }
}
