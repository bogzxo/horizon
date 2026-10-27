using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The gutter down the left of the code, in fixed columns with the same gaps between them whatever is in them. A column
// for a breakpoint, what's wrong and the debugger's arrow, the numbers, the chevron of what folds and the bar of what
// changed, then the code. The marks hung off the left edge and the numbers off the right with whatever room was left
// between them, a warning's triangle sat next to one number and an error's cross a long way from another, and the
// change's bar was painted over the chevron's room.
public partial class CodeEditor
{
    // How far in from the left the column of marks starts, and the gap after it, a share of a digit
    private const float GLYPH_PAD = 2.0f, GLYPH_GAP = 0.6f;

    /// <summary>Where the columns of the gutter are, across from the left of the editor's content and the left of the code.</summary>
    private readonly record struct GutterColumns(float GlyphLeft, float GlyphSide, float NumbersRight, float FoldLeft, float FoldSide, float BarLeft, float BarRight);

    // The columns for where the code starts, every number of them worked out the one way, see the top of the file
    private GutterColumns GutterOf(UIRect content, float codeLeft, float digit, float lineHeight)
    {
        float glyph = MathF.Round(lineHeight * 0.8f);
        float numbersRight = codeLeft - digit * GUTTER_GAP;
        float bar = MathF.Max(2.0f, MathF.Round(digit * CHANGE_BAR));
        float barRight = codeLeft - MathF.Max(1.0f, MathF.Round(digit * 0.15f));
        float barLeft = barRight - bar;
        float foldFrom = numbersRight + MathF.Max(2.0f, MathF.Round(digit * 0.4f));
        float foldTo = barLeft - MathF.Max(2.0f, MathF.Round(digit * 0.25f));
        // The chevron's box a bit wider than its column, the icon is drawn well inside its box and came out a speck
        float fold = MathF.Round(lineHeight * 0.9f);
        return new GutterColumns(content.Min.X + GLYPH_PAD, glyph, numbersRight, foldFrom + (foldTo - foldFrom - fold) * 0.5f, fold, barLeft, barRight);
    }

    /// <summary>
    /// A diff's two columns of numbers, every line's in the old version and in the new one counted from 1, 0 where it isn't
    /// in one of them (a line taken out has no new number). Null for one column of the lines' own numbers.
    /// </summary>
    public (int[] Old, int[] New)? DiffLineNumbers
    {
        get => diffNumbers;
        set
        {
            diffNumbers = value;
            diffBiggest = 0;
            if (value is var (olds, news))
            {
                foreach (int number in olds) diffBiggest = Math.Max(diffBiggest, number);
                foreach (int number in news) diffBiggest = Math.Max(diffBiggest, number);
            }
        }
    }

    // The diff's numbers and the biggest of them, worked out once they're given and not every time the gutter's measured
    private (int[] Old, int[] New)? diffNumbers;
    private int diffBiggest;

    // How many digits a column of numbers is wide, the biggest number's at the least four. At the least two, an enter at
    // line 99 or an undo back past it moved every line of the code a digit right and back again. A diff isn't typed into
    // and has two columns of them, it keeps to two
    private const int MIN_DIGITS = 4;

    private int NumberDigits()
    {
        int count = diffNumbers is null ? document.LineCount + FirstLineNumber - 1 : diffBiggest, digits = 1;
        while ((count /= 10) > 0) digits++;
        return Math.Max(diffNumbers is null ? MIN_DIGITS : 2, digits);
    }

    private float GutterWidth(UISkin skin, float scale)
    {
        if (!LineNumbers)
            return TEXT_PAD;

        // Room for the biggest number there is, every digit of a font is as wide as its zero, the column of marks before
        // them and the chevron and the change's bar after. A diff has two columns of them with a digit between
        int digits = NumberDigits();
        float numbers = DiffLineNumbers is null ? digits : digits * 2 + 1;
        float digit = FontOf(skin).Measure("0", scale, markup: false).X;
        float glyph = MathF.Round(FontOf(skin).LineHeight * scale * 0.8f);
        return GLYPH_PAD + glyph + MathF.Round(digit * GLYPH_GAP) + digit * (numbers + GUTTER_GAP);
    }

    // A line's number written right against a place, nothing for a 0
    private static void Number(UIDrawList list, Span<char> into, int value, float right, float top, float scale, float digit, Vector4 shade)
    {
        if (value > 0 && value.TryFormat(into, out int length))
            list.Text(into[..length], new Vector2(right - digit * length, top), scale, shade, markup: false);
    }

    // The gutter's rows in sight, each line's number, its mark and its chevron, and the bars of what changed
    private void PaintGutter(UIDrawList list, UISkin skin, float scale, UIRect content, UIRect area, float lineHeight, float digit, int first, int last, float origin, UISyntaxColors colors)
    {
        list.PushClip(new UIRect(content.Min, new Vector2(area.Min.X, content.Max.Y)));
        Vector2 pointerAt = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : Vector2.Zero;
        bool overGutter = IsHovered && pointerAt.X < area.Min.X;
        float pointerY = pointerAt.Y;
        GutterColumns columns = GutterOf(content, area.Min.X, digit, lineHeight);
        Vector4 chevron = Vector4.Lerp(colors.LineNumber, colors.Name, 0.45f);
        Span<char> number = stackalloc char[12];
        int digits = NumberDigits();

        for (int index = first; index <= last; index++)
        {
            CodeRow row = rows[index];

            // A row of reference counts over a line has no number, no mark and no change of its own
            if (row.Lens)
                continue;

            // A change's bar goes down every row of its line, painted once from its first row (or the first in sight)
            if ((changedLines.Count > 0 || deletedBefore.Count > 0) && (row.Start == 0 || index == first))
                PaintLineChange(list, skin, area, index, origin, lineHeight, digit, columns);

            if (row.Start > 0)
                continue;

            int line = row.Line;
            list.BeginPart(PART_GUTTER + line);
            float top = FlowTop(line, RowTop(area, index, lineHeight), origin);
            Vector4 shade = line == document.Caret.Line ? colors.Name : colors.LineNumber;

            // What is wrong has its icon in the column of marks, the number lit the same colour. A breakpoint has that
            // place before it, and the line a debugger stopped on its arrow over that
            bool breakpoint = Breakpoints?.Contains(line) == true;
            bool marked = IsMarked(line, out bool warning);
            if (marked)
                shade = warning ? skin.WarningColor : skin.ErrorColor;

            float middle = top - lineHeight * 0.5f;
            var slot = new UIRect(new Vector2(columns.GlyphLeft, middle - columns.GlyphSide * 0.5f), new Vector2(columns.GlyphLeft + columns.GlyphSide, middle + columns.GlyphSide * 0.5f));
            bool pointing = OnGutterPressed is not null && overGutter && pointerY <= top && pointerY > top - lineHeight;
            if (breakpoint || pointing)
            {
                float dot = MathF.Round(columns.GlyphSide * 0.32f);
                var round = new UIRect(slot.Center - new Vector2(dot), slot.Center + new Vector2(dot));
                list.RoundRect(round, dot, skin.ErrorColor with { W = breakpoint ? 1.0f : 0.35f });
            }
            else if (marked)
            {
                list.Symbol(warning ? "warning" : "error", slot, shade);
            }

            if (line == ExecutionLine)
                list.Symbol("debug-stackframe", slot, skin.WarningColor);

            // The number of a row cut off by the bottom edge fades out with its code
            bool cut = top - lineHeight < area.Min.Y - 0.5f;
            if (cut)
                list.PushFade(area, 0.0f, lineHeight * BOTTOM_FADE);
            if (diffNumbers is var (olds, news))
            {
                Number(list, number, line < olds.Length ? olds[line] : 0, columns.NumbersRight - digit * (digits + 1), top, scale, digit, shade);
                Number(list, number, line < news.Length ? news[line] : 0, columns.NumbersRight, top, scale, digit, shade);
            }
            else
            {
                Number(list, number, line + FirstLineNumber, columns.NumbersRight, top, scale, digit, shade);
            }
            if (cut)
                list.PopFade();

            // What can fold has its chevron in its column after the number, always when it's folded, under the pointer otherwise
            bool isFolded = folded.Contains(line);
            if ((isFolded || overGutter) && foldable.ContainsKey(line))
            {
                var box = new UIRect(new Vector2(columns.FoldLeft, middle - columns.FoldSide * 0.5f), new Vector2(columns.FoldLeft + columns.FoldSide, middle + columns.FoldSide * 0.5f));
                list.Symbol(isFolded ? "chevron-right" : "chevron-down", box, isFolded ? colors.Name : chevron);
            }
        }

        list.PopClip();
    }
}
