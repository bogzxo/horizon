using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>Where the spaces and tabs of the code are drawn as faint marks, VS Code's editor.renderWhitespace.</summary>
public enum UIWhitespace
{
    /// <summary>Nowhere, they're just room.</summary>
    None,

    /// <summary>Only inside what's selected.</summary>
    Selection,

    /// <summary>The runs a line starts and ends with, the indentation and what's left trailing.</summary>
    Boundary,

    /// <summary>Every one of them.</summary>
    All
}

// The spaces and tabs drawn, a dot in the middle of a space and an arrow along a tab, so what a line is indented with
// and what's trailing at its end can be seen. Only the rows in sight, a run at a time, where it starts measured once and
// the rest of it stepped along by the width of a space or a tab, and nothing made while it draws
public partial class CodeEditor
{
    /// <summary>Where the spaces and tabs are drawn, nowhere unless the program says.</summary>
    public UIWhitespace RenderWhitespace { get; set; }

    /// <summary>Helper method to draw the whitespace of the rows in sight that's wanted, under the text.</summary>
    private void PaintWhitespace(UIDrawList list, UISkin skin, float scale, float left, UIRect area, float lineHeight, int first, int last)
    {
        UIWhitespace mode = RenderWhitespace;
        if (mode == UIWhitespace.None || rows.Count == 0)
            return;
        if (mode == UIWhitespace.Selection && !document.HasSelection && !HasExtraSelection())
            return;

        float space = FontOf(skin).Measure(" ", scale, markup: false).X;
        float tab = TabWidth(skin, scale);
        float dot = MathF.Max(1.0f, MathF.Round(scale * 2.5f));
        Vector4 colour = skin.TextColor with { W = 0.28f };

        for (int index = first; index <= last; index++)
        {
            CodeRow row = rows[index];
            string text = document.LineAt(row.Line);
            int rowEnd = Math.Min(row.End, text.Length);
            if (rowEnd <= row.Start)
                continue;
            list.BeginPart(RowPart(ROW_WHITESPACE, row));

            // Where the line's own text starts and ends, the runs either side of it being the boundary ones
            int lead = 0;
            while (lead < text.Length && text[lead] is ' ' or '\t') lead++;
            int trail = text.Length;
            while (trail > lead && text[trail - 1] is ' ' or '\t') trail--;

            float middle = RowTop(area, index, lineHeight) - lineHeight * 0.5f;
            int at = row.Start;
            while (at < rowEnd)
            {
                if (text[at] is not (' ' or '\t'))
                {
                    at++;
                    continue;
                }

                // A run of them, its start measured and the rest stepped along
                float x = left + ColumnX(skin, scale, row.Line, at);
                for (; at < rowEnd && text[at] is ' ' or '\t'; at++)
                {
                    bool tabbed = text[at] == '\t';
                    float width = tabbed ? tab : space;
                    if (x + width >= area.Min.X && x <= area.Max.X && Wanted(mode, row.Line, at, lead, trail))
                    {
                        if (tabbed)
                        {
                            PaintTabArrow(list, x, width, middle, dot, colour);
                        }
                        else
                        {
                            var corner = new Vector2(MathF.Round(x + (width - dot) * 0.5f), MathF.Round(middle - dot * 0.5f));
                            list.Rect(new UIRect(corner, corner + new Vector2(dot)), colour);
                        }
                    }

                    x += width;
                }
            }
        }
    }

    // Whether a space or tab of a line is drawn, by where it is and what's selected
    private bool Wanted(UIWhitespace mode, int line, int column, int lead, int trail) => mode switch
    {
        UIWhitespace.All => true,
        UIWhitespace.Boundary => column < lead || column >= trail,
        UIWhitespace.Selection => IsSelected(line, column),
        _ => false
    };

    // Whether a character is inside one of the selections, the caret's or one of the others'
    private bool IsSelected(int line, int column)
    {
        var at = new UITextPosition(line, column);
        if (document.HasSelection && at >= document.SelectionStart && at < document.SelectionEnd)
            return true;

        // By index, a foreach over the interface would make an enumerator every frame
        var extras = document.ExtraCarets;
        for (int i = 0; i < extras.Count; i++)
        {
            if (!extras[i].IsEmpty && at >= extras[i].Start && at < extras[i].End)
                return true;
        }

        return false;
    }

    private bool HasExtraSelection()
    {
        var extras = document.ExtraCarets;
        for (int i = 0; i < extras.Count; i++)
        {
            if (!extras[i].IsEmpty)
                return true;
        }

        return false;
    }

    // An arrow along a tab, a line a pixel thick with a little head at its end built of rows a pixel apart
    private static void PaintTabArrow(UIDrawList list, float x, float width, float middle, float dot, Vector4 colour)
    {
        float from = MathF.Round(x + width * 0.15f), to = MathF.Round(x + width * 0.85f);
        float y = MathF.Round(middle);
        list.Rect(new UIRect(new Vector2(from, y), new Vector2(to, y + 1.0f)), colour);

        int head = (int)MathF.Max(2.0f, dot + 1.0f);
        for (int step = 1; step <= head; step++)
            list.Rect(new UIRect(new Vector2(to - step, y - step), new Vector2(to - step + 1.0f, y + 1.0f + step)), colour);
    }
}
