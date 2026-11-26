using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>A piece of a line with a colour behind it, the words a diff changed inside a changed line.</summary>
/// <param name="Line">Which line, counted from 0.</param>
/// <param name="Column">Where on it it starts, counted from 0.</param>
/// <param name="Length">How many characters it covers.</param>
/// <param name="Colour">What goes behind them, see through so the line's own colour shows under it.</param>
public readonly record struct UICodeRangeHighlight(int Line, int Column, int Length, Vector4 Colour);

// Pieces of lines lit behind the text, over the colour a whole line gets, so a word that changed stands out of the
// line it changed in. A piece over a wrapped line is lit on each row it's on
public partial class CodeEditor
{
    /// <summary>The pieces of lines with a colour behind them, under the selection. Empty for none.</summary>
    public IReadOnlyList<UICodeRangeHighlight> RangeHighlights
    {
        get => rangeHighlights;
        set
        {
            rangeHighlights = value ?? [];
            rangeHighlightsSorted.Clear();
            if (rangeHighlights.Count > 0)
            {
                if (rangeHighlightsSorted.Capacity < rangeHighlights.Count)
                    rangeHighlightsSorted.Capacity = rangeHighlights.Count;
                for (int i = 0; i < rangeHighlights.Count; i++)
                    rangeHighlightsSorted.Add(rangeHighlights[i]);
                rangeHighlightsSorted.Sort(static (a, b) => a.Line.CompareTo(b.Line));
            }
        }
    }

    private IReadOnlyList<UICodeRangeHighlight> rangeHighlights = [];
    private readonly List<UICodeRangeHighlight> rangeHighlightsSorted = [];

    private int FirstRangeHighlightAtOrAfter(int line)
    {
        int low = 0, high = rangeHighlightsSorted.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (rangeHighlightsSorted[mid].Line < line)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    /// <summary>Helper method to paint the pieces lit on the lines in sight, a box for each row a piece is on.</summary>
    private void PaintRanges(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int firstLine, int lastLine)
    {
        if (rangeHighlightsSorted.Count == 0)
            return;

        for (int i = FirstRangeHighlightAtOrAfter(firstLine); i < rangeHighlightsSorted.Count; i++)
        {
            UICodeRangeHighlight range = rangeHighlightsSorted[i];
            if (range.Line > lastLine)
                break;
            if (range.Length <= 0 || range.Line < 0 || range.Line >= document.LineCount || IsHidden(range.Line, out _))
                continue;

            string text = document.LineAt(range.Line);
            int from = Math.Clamp(range.Column, 0, text.Length), to = Math.Clamp(range.Column + range.Length, 0, text.Length);
            while (from < to)
            {
                // To the end of the row the piece starts on, bounded by that row's own End rather than the next row's Start
                // so virtual rows (predicted/block rows) after a line never truncate the highlight.
                int row = RowOf(range.Line, from);
                int rowEnd = row < rows.Count && rows[row].Line == range.Line ? Math.Min(rows[row].End, text.Length) : text.Length;
                int end = Math.Min(to, rowEnd);
                if (end <= from)
                    break;

                list.Rect(Piece(skin, scale, area, left, lineHeight, range.Line, from, end), range.Colour);
                from = end;
            }
        }
    }
}
