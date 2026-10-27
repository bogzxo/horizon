using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// Something said over a line on a row of its own, Visual Studio's code lens, how many places use what the line declares.
/// Several on one line (two names declared in one go) go side by side with a bar between them.
/// </summary>
/// <param name="Line">The line it's over, counted from 0.</param>
/// <param name="Column">Where on the line the thing it's about is, which puts several on one line in order.</param>
/// <param name="Text">What it says, "3 references". Empty keeps its row with nothing written on it yet.</param>
/// <param name="Id">Whatever the program wants handed back when it's pressed.</param>
public readonly record struct UICodeLensAbove(int Line, int Column, string Text, int Id = 0);

// The lenses over the lines, each line with any getting a row of its own above it (CodeEditor.Rows makes the rows, a lens
// row being one that has no text of the line on it). The caret never lands on one, the line numbers skip it, and what's
// written on it is small and dim, starting where the line's own text does. The one under the pointer is underlined and a
// press on it goes to the program. They move along with the lines put in or taken out above them until the program says
// them again, so the code doesn't jump about while it's typed into
public partial class CodeEditor
{
    // How big a lens over a line is written against the code, and the paint parts its rows go in (counted down from here
    // by line, under every other part the editor has)
    private const float LENS_ABOVE_TEXT = 0.8f;
    private const int PART_LENS_ABOVE = -11;

    /// <summary>The lenses over the lines, in no particular order. Empty for none, and no line gets a row over it.</summary>
    public IReadOnlyList<UICodeLensAbove> LensesAbove
    {
        get => lensesAbove;
        set
        {
            lensesAboveNext.Clear();
            if (value is not null)
                lensesAboveNext.AddRange(value);
            lensesAboveNext.Sort(ByPlace);

            // Only lenses on other lines make the rows again, new words on the same ones are only painted
            if (!SameLines(lensesAbove, lensesAboveNext))
                lensRowsChanged++;
            (lensesAbove, lensesAboveNext) = (lensesAboveNext, lensesAbove);
            lensesAboveNext.Clear();
            lensAboveBoxes.Clear();
        }
    }

    private List<UICodeLensAbove> lensesAbove = [], lensesAboveNext = [];

    // A count of the changes to which lines have lenses, what the rows are made again for
    private int lensRowsChanged;

    /// <summary>Called with the lens pressed and where it was painted, for a list of what it counts to come up under it.</summary>
    public Action<UICodeLensAbove, UIRect>? OnLensAbovePressed { get; set; }

    // Where each lens was painted last, by its index, for the press and the underline to go by
    private readonly List<(int Lens, UIRect Box)> lensAboveBoxes = [];

    private static int ByPlace(UICodeLensAbove a, UICodeLensAbove b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column);

    // Whether two sorted lists of lenses are over the same lines
    private static bool SameLines(List<UICodeLensAbove> a, List<UICodeLensAbove> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Line != b[i].Line)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a line has a lens over it, walking a cursor along the sorted lenses as the rows are made top to bottom, so
    /// making them stays one pass however many there are.
    /// </summary>
    private bool LensAboveOn(int line, ref int cursor)
    {
        while (cursor < lensesAbove.Count && lensesAbove[cursor].Line < line)
            cursor++;
        return cursor < lensesAbove.Count && lensesAbove[cursor].Line == line;
    }

    // The first lens over a line, -1 for none, looked for by halves
    private int FirstLensAbove(int line)
    {
        int low = 0, high = lensesAbove.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            int at = lensesAbove[middle].Line;
            if (at < line)
            {
                low = middle + 1;
            }
            else
            {
                if (at == line)
                    found = middle;
                high = middle - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// Lines put in or taken out, the lenses after them moved along with their lines. One whose line was taken out goes,
    /// and a line pushed down by an enter before its text takes its lens down with it.
    /// </summary>
    private void MoveLensesAbove(UITextPosition at, int by)
    {
        if (lensesAbove.Count == 0 || by == 0)
            return;

        bool blankBefore = by > 0 && at.Line < document.LineCount && string.IsNullOrWhiteSpace(document.LineAt(at.Line));
        lensesAboveNext.Clear();
        for (int i = 0; i < lensesAbove.Count; i++)
        {
            UICodeLensAbove lens = lensesAbove[i];
            int line = lens.Line;
            if (by > 0 && (line > at.Line || (line == at.Line && blankBefore)))
            {
                line += by;
            }
            else if (by < 0 && line > at.Line)
            {
                line += by;
            }
            else if (by < 0 && line == at.Line)
            {
                // The end of what was taken out joins the line it starts on, its lens with it unless that one has its own
                line = at.Line + by;
                if (FirstLensAbove(line) >= 0)
                    continue;
            }
            else if (by < 0 && line > at.Line + by)
            {
                continue;
            }

            lensesAboveNext.Add(lens with { Line = line });
        }

        lensesAboveNext.Sort(ByPlace);
        (lensesAbove, lensesAboveNext) = (lensesAboveNext, lensesAbove);
        lensesAboveNext.Clear();
        lensAboveBoxes.Clear();
        lensRowsChanged++;
    }

    /// <summary>The first and last lines in sight as the code was last painted, for what's only worked out for those. Last is -1 before the first paint.</summary>
    public (int First, int Last) LinesInSight
    {
        get
        {
            if (paintedSkin is not { } skin || rows.Count == 0)
                return (0, -1);

            float lineHeight = FontOf(skin).LineHeight * paintedScale;
            UIRect area = TextArea(skin, paintedScale);
            int first = Math.Clamp((int)(scrollY / lineHeight), 0, rows.Count - 1);
            int last = Math.Min(rows.Count - 1, first + (int)(area.Height / lineHeight) + 1);
            int lines = Math.Max(1, document.LineCount);
            return (Math.Min(rows[first].Line, lines - 1), Math.Min(rows[last].Line, lines - 1));
        }
    }

    /// <summary>How much room the caret's line has over it for its lens, so a line scrolled to from below shows its lens too.</summary>
    private float LensRoomAbove(int line, int column, float lineHeight)
    {
        int row = RowOf(line, column);
        return row > 0 && row < rows.Count && rows[row].Start == 0 && rows[row - 1].Lens && rows[row - 1].Line == line ? lineHeight : 0.0f;
    }

    /// <summary>Whether a point is on a lens row, where the tooltip and the hover say nothing.</summary>
    private bool OverLensRow(Vector2 point)
    {
        if (lensesAbove.Count == 0 || paintedSkin is not { } skin || rows.Count == 0)
            return false;

        float lineHeight = FontOf(skin).LineHeight * paintedScale;
        UIRect area = TextArea(skin, paintedScale);
        int index = (int)MathF.Floor((area.Max.Y + scrollY - point.Y) / lineHeight);
        return index >= 0 && index < rows.Count && rows[index].Lens;
    }

    /// <summary>The lens under a point and where it was painted, from the last paint.</summary>
    public bool LensAboveAt(Vector2 point, out UICodeLensAbove lens, out UIRect box)
    {
        for (int i = 0; i < lensAboveBoxes.Count; i++)
        {
            var (index, painted) = lensAboveBoxes[i];
            if (index < lensesAbove.Count && painted.Shrink(new UIEdges(-1.0f)).Contains(point))
            {
                lens = lensesAbove[index];
                box = painted;
                return true;
            }
        }

        lens = default;
        box = default;
        return false;
    }

    /// <summary>Where the lens of an index among those over a line was painted last, for a test to press. Empty when it wasn't.</summary>
    public UIRect LensAboveBounds(int line, int index)
    {
        int seen = 0;
        for (int i = 0; i < lensAboveBoxes.Count; i++)
        {
            var (at, box) = lensAboveBoxes[i];
            if (at < lensesAbove.Count && lensesAbove[at].Line == line && seen++ == index)
                return box;
        }

        return default;
    }

    /// <summary>Helper method for a press on a lens over a line, handed to the program, true when it took the press.</summary>
    private bool PressLensAbove(Vector2 point)
    {
        if (OnLensAbovePressed is not { } pressed || !LensAboveAt(point, out UICodeLensAbove lens, out UIRect box))
            return false;

        pressed(lens, box);
        return true;
    }

    /// <summary>Helper method to paint the lens rows in sight, each lens small and dim from where its line's text starts.</summary>
    private void PaintLensesAbove(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int first, int last, UISyntaxColors colors)
    {
        lensAboveBoxes.Clear();
        if (lensesAbove.Count == 0)
            return;

        UIFont font = FontOf(skin);
        float small = scale * LENS_ABOVE_TEXT;
        float height = font.LineHeight * small;

        // Grey like the line numbers, the way Visual Studio's are, and a little nearer the code's own colour under the pointer
        Vector4 dim = colors.LineNumber;
        Vector4 lit = Vector4.Lerp(colors.LineNumber, colors.Name, 0.45f);
        Vector4 bar = colors.LineNumber with { W = colors.LineNumber.W * 0.5f };
        Vector2 pointer = IsHovered && Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : new Vector2(float.MinValue);
        float gap = font.Measure(LENS_GAP, small, markup: false).X;

        for (int index = first; index <= last && index < rows.Count; index++)
        {
            CodeRow row = rows[index];
            int at = row.Lens ? FirstLensAbove(row.Line) : -1;
            if (at < 0 || row.Line >= document.LineCount)
                continue;

            list.BeginPart(PART_LENS_ABOVE - row.Line);
            string text = document.LineAt(row.Line);
            int indent = 0;
            while (indent < text.Length && text[indent] is ' ' or '\t')
                indent++;

            // Down against the line it's over, the way Visual Studio's sits on its declaration
            float x = left + ColumnX(skin, scale, row.Line, indent);
            float top = RowTop(area, index, lineHeight) - (lineHeight - height);
            bool any = false;
            for (int i = at; i < lensesAbove.Count && lensesAbove[i].Line == row.Line; i++)
            {
                string said = lensesAbove[i].Text;
                if (said.Length == 0)
                    continue;

                if (any)
                {
                    list.Text(LENS_GAP, new Vector2(x, top), small, bar, markup: false);
                    x += gap;
                }

                float width = font.Measure(said, small, markup: false).X;
                var box = new UIRect(new Vector2(x, top - height), new Vector2(x + width, top));
                lensAboveBoxes.Add((i, box));
                bool hovered = OnLensAbovePressed is not null && box.Contains(pointer);
                list.Text(said, new Vector2(x, top), small, hovered ? lit : dim, markup: false);

                // The one under the pointer underlined, the way a link is
                if (hovered)
                {
                    float under = top - height + MathF.Max(1.0f, MathF.Round(height * 0.1f));
                    list.Rect(new UIRect(new Vector2(x, under), new Vector2(x + width, under + MathF.Max(1.0f, MathF.Round(scale)))), lit);
                }

                x += width;
                any = true;
            }
        }
    }
}
