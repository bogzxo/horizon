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
        set => rangeHighlights = value ?? [];
    }

    private IReadOnlyList<UICodeRangeHighlight> rangeHighlights = [];

    /// <summary>Helper method to paint the pieces lit on the lines in sight, a box for each row a piece is on.</summary>
    private void PaintRanges(UIDrawList list, UISkin skin, float scale, UIRect area, float left, float lineHeight, int firstLine, int lastLine)
    {
        for (int i = 0; i < rangeHighlights.Count; i++)
        {
            UICodeRangeHighlight range = rangeHighlights[i];
            if (range.Length <= 0 || range.Line < firstLine || range.Line > lastLine || range.Line >= document.LineCount || IsHidden(range.Line, out _))
                continue;

            string text = document.LineAt(range.Line);
            int from = Math.Clamp(range.Column, 0, text.Length), to = Math.Clamp(range.Column + range.Length, 0, text.Length);
            while (from < to)
            {
                // To the end of the row the piece starts on, the next row's start when the line is wrapped there
                int row = RowOf(range.Line, from);
                int rowEnd = row + 1 < rows.Count && rows[row + 1].Line == range.Line ? rows[row + 1].Start : int.MaxValue;
                int end = Math.Min(to, rowEnd);

                float top = TopOf(area, range.Line, from, lineHeight);
                float x0 = ColumnX(skin, scale, range.Line, from);
                float x1 = ColumnX(skin, scale, range.Line, end - 1) + Width(skin, scale, text.AsSpan(end - 1, 1));
                list.Rect(new UIRect(new Vector2(left + x0, top - lineHeight), new Vector2(left + x1, top)), range.Colour);
                from = end;
            }
        }
    }
}
