using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The minimap, the whole of the code drawn small down the right between the text and the scroll bar, VS Code's and
// Sublime's. Every row is a strip a couple of units tall with its words as little bars in their colours, the part in
// sight under a lighter slider that's dragged to scroll, a click anywhere else goes there. Down its left edge the bars of
// the lines that changed (LineChanges), and over it what was found and what's wrong, so a long file's busy places show
// at a glance. Taller than the view, it scrolls along with the code so the slider never leaves it.
public partial class CodeEditor
{
    // How wide it is, and how big a character and a row are drawn in it, in the editor's units
    private const float MINIMAP_WIDTH = 96.0f, MINIMAP_CHAR = 1.0f, MINIMAP_ROW = 2.0f;

    // The most characters of a row drawn, past that it runs off the edge like it would anyway
    private const int MINIMAP_COLUMNS = 92;

    private const int PART_MINIMAP = -10;

    /// <summary>Whether the code is drawn small down the right, a slider over what's in sight. Off unless the program turns it on.</summary>
    public bool Minimap { get; set; }

    // Where the slider was taken from, as a share of the slider's height, while it's dragged
    private bool draggingMinimap;
    private float minimapGrab;

    private float MinimapWidth => Minimap ? MINIMAP_WIDTH : 0.0f;

    /// <summary>Helper method for where the minimap is, between the text and the scroll bar.</summary>
    private UIRect MinimapArea(UISkin skin, float scale)
    {
        UIRect area = TextArea(skin, scale);
        return new UIRect(new Vector2(area.Max.X, area.Min.Y), new Vector2(area.Max.X + MinimapWidth, area.Max.Y));
    }

    /// <summary>
    /// Helper method for how far the minimap itself is scrolled, as much of it as the code is, so that the slider is at the
    /// top with the code at the top and at the bottom with the code at the bottom.
    /// </summary>
    private float MinimapScroll(UIRect map, float lineHeight)
    {
        float content = rows.Count * MINIMAP_ROW;
        if (content <= map.Height)
            return 0.0f;

        float most = MathF.Max(1.0f, rows.Count * lineHeight - map.Height * 0.5f);
        return (content - map.Height) * Math.Clamp(scrollY / most, 0.0f, 1.0f);
    }

    /// <summary>Helper method for the slider's top, down from the minimap's top, for a scroll of the code.</summary>
    private float SliderDown(UIRect map, float lineHeight, float scroll)
    {
        float content = rows.Count * MINIMAP_ROW;
        float most = MathF.Max(1.0f, rows.Count * lineHeight - map.Height * 0.5f);
        float mapScroll = content <= map.Height ? 0.0f : (content - map.Height) * Math.Clamp(scroll / most, 0.0f, 1.0f);
        return scroll / lineHeight * MINIMAP_ROW - mapScroll;
    }

    private void PaintMinimap(UIDrawList list, UISkin skin, float scale, UISyntaxColors colors, float lineHeight)
    {
        if (!Minimap || rows.Count == 0)
            return;

        UIRect map = MinimapArea(skin, scale);
        float mapScroll = MinimapScroll(map, lineHeight);
        int first = Math.Clamp((int)(mapScroll / MINIMAP_ROW), 0, rows.Count - 1);
        int last = Math.Min(rows.Count - 1, first + (int)(map.Height / MINIMAP_ROW) + 1);

        // Its rows stand for other lines from one tick to the next as it scrolls, nothing of it is blended between ticks
        list.BeginPart(PART_MINIMAP);
        list.BeginSteps();
        list.PushClip(map);
        list.Rect(map, skin.TextColor with { W = 0.025f });

        // The words of a line are worked out once and kept by its text, typing into a big file paints a few hundred rows
        // of these every tick and going through every span of every row again was most of a tick. Kept across colourings,
        // which every key starts, and let go of when the theme's colours change
        if (!ReferenceEquals(minimapColoursFor, colors))
        {
            minimapWords.Clear();
            minimapTodos.Clear();
            minimapColoursFor = colors;
        }

        float left = map.Min.X + 4.0f;
        for (int index = first; index <= last; index++)
        {
            CodeRow row = rows[index];
            float top = map.Max.Y - (index * MINIMAP_ROW - mapScroll);
            string text = document.LineAt(row.Line);
            MinimapWord[] words = WordsOf(row.Line, text, colors);

            // A wrapped row starts its columns again where it starts in the line
            int rowColumn = row.Start == 0 ? 0 : Column(text, 0, row.Start);
            foreach (MinimapWord word in words)
            {
                if (word.End <= row.Start || word.Start >= row.End)
                    continue;

                int from = Math.Max(word.StartColumn, rowColumn) - rowColumn;
                int to = Math.Min(MINIMAP_COLUMNS, Math.Min(word.EndColumn, row.End >= text.Length ? word.EndColumn : Column(text, 0, row.End)) - rowColumn);
                if (to > from)
                    list.Rect(new UIRect(new Vector2(left + from * MINIMAP_CHAR, top - MINIMAP_ROW + 0.4f), new Vector2(left + to * MINIMAP_CHAR, top)), word.Colour);
            }
        }

        PaintMinimapMarks(list, skin, map, mapScroll, first, last);

        // The slider over what's in sight, lighter under the pointer and while it's held
        float down = SliderDown(map, lineHeight, scrollY);
        float height = MathF.Max(MINIMAP_ROW * 2.0f, map.Height / lineHeight * MINIMAP_ROW);
        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : Vector2.Zero;
        var slider = new UIRect(new Vector2(map.Min.X, map.Max.Y - down - height), new Vector2(map.Max.X, map.Max.Y - down));
        bool lit = draggingMinimap || (IsHovered && map.Contains(pointer));
        list.Rect(slider, skin.TextColor with { W = lit ? 0.2f : 0.12f });

        list.PopClip();
        list.EndSteps();
    }

    /// <summary>A word of a line as the minimap draws it, where it starts and ends in the line and its colour.</summary>
    private readonly record struct MinimapWord(int Start, int End, int StartColumn, int EndColumn, Vector4 Colour);

    // The words of the lines drawn, by the line's text, for the colours they were worked out with
    private readonly Dictionary<string, MinimapWord[]> minimapWords = new(StringComparer.Ordinal);

    // Whether a line has a TODO (0) or a FIXME (1) or neither (-1), by its text, kept like its words
    private readonly Dictionary<string, sbyte> minimapTodos = new(StringComparer.Ordinal);
    private object? minimapColoursFor;

    /// <summary>
    /// Helper method for the words of a line, every run of what isn't white space one bar, the colour of its first piece
    /// that isn't punctuation. A word of five tokens (thing_1: {) is one bar, not five.
    /// </summary>
    private MinimapWord[] WordsOf(int line, string text, UISyntaxColors colors)
    {
        if (minimapWords.TryGetValue(text, out MinimapWord[]? known))
            return known;

        List<SyntaxSpan>? spans = ColoursOf(line, text);
        var words = new List<MinimapWord>();
        int at = 0, column = 0;
        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                column += text[at++] == '\t' ? 4 : 1;
            int start = at, startColumn = column;
            while (at < text.Length && !char.IsWhiteSpace(text[at]))
            {
                at++;
                column++;
            }
            if (at > start)
                words.Add(new MinimapWord(start, at, startColumn, column, Faded(KindAt(spans, start, at), colors)));
        }

        MinimapWord[] made = [.. words];

        // Lines not coloured yet aren't kept, they'd stay plain once they are
        if (spans is not null)
        {
            if (minimapWords.Count > 20000)
                minimapWords.Clear();
            minimapWords[text] = made;
        }

        return made;
    }

    // The kind of the first piece of a word that isn't punctuation, a name for one that's all punctuation or not coloured
    private static SyntaxKind KindAt(List<SyntaxSpan>? spans, int start, int end)
    {
        if (spans is null)
            return SyntaxKind.Name;

        SyntaxKind? punctuation = null;
        foreach (SyntaxSpan span in spans)
        {
            if (span.Start + span.Length <= start) continue;
            if (span.Start >= end) break;
            if (span.Kind != SyntaxKind.Punctuation) return span.Kind;
            punctuation ??= span.Kind;
        }

        return punctuation ?? SyntaxKind.Name;
    }

    private static Vector4 Faded(SyntaxKind kind, UISyntaxColors colors)
    {
        Vector4 colour = colors.Of(kind);
        return colour with { W = colour.W * 0.65f };
    }

    /// <summary>Helper method for whether a line has a FIXME (1), a TODO (0) or neither (-1), worked out once a text and kept.</summary>
    private sbyte TodoKindOf(int line)
    {
        string text = document.LineAt(line);
        if (minimapTodos.TryGetValue(text, out sbyte known))
            return known;

        sbyte kind = -1;
        foreach (var (_, _, urgent) in TodosOn(line))
            kind = Math.Max(kind, urgent ? (sbyte)1 : (sbyte)0);

        // A line not coloured yet isn't kept, it can't be told whether its TODO is in a comment
        if (ColoursOf(line, text) is not null)
        {
            if (minimapTodos.Count > 20000)
                minimapTodos.Clear();
            minimapTodos[text] = kind;
        }

        return kind;
    }

    // How many columns in a place of a row is, a tab being four
    private static int Column(string text, int rowStart, int at)
    {
        int column = 0;
        for (int i = rowStart; i < at && i < text.Length; i++)
            column += text[i] == '\t' ? 4 : 1;
        return column;
    }

    /// <summary>Helper method for the changes down the left edge, and what was found and what's wrong across the rows.</summary>
    private void PaintMinimapMarks(UIDrawList list, UISkin skin, UIRect map, float mapScroll, int first, int last)
    {
        for (int index = first; index <= last; index++)
        {
            CodeRow row = rows[index];
            float top = map.Max.Y - (index * MINIMAP_ROW - mapScroll);

            if (changedLines.TryGetValue(row.Line, out UILineChangeKind kind))
                list.Rect(new UIRect(new Vector2(map.Min.X, top - MINIMAP_ROW), new Vector2(map.Min.X + 2.0f, top)), ChangeColour(kind, skin));
            if (row.Start == 0 && deletedBefore.Contains(row.Line))
                list.Rect(new UIRect(new Vector2(map.Min.X, top - 1.0f), new Vector2(map.Min.X + 4.0f, top + 1.0f)), skin.ErrorColor);

            // A TODO or a FIXME, a mark down the right edge in its colour
            if (HighlightTodos && row.Start == 0 && TodoKindOf(row.Line) is var todo and >= 0)
                list.Rect(new UIRect(new Vector2(map.Max.X - 6.0f, top - MINIMAP_ROW), new Vector2(map.Max.X, top)), todo > 0 ? skin.ErrorColor : skin.WarningColor);

            if (row.Line == document.Caret.Line && row.Start == 0)
                list.Rect(new UIRect(new Vector2(map.Min.X, top - MINIMAP_ROW), new Vector2(map.Max.X, top)), skin.TextColor with { W = 0.22f });
        }

        // What was found, and what's wrong, across the rows they're on
        if (findQuery.Length > 0)
        {
            Vector4 lit = skin.HighlightColor with { W = 0.5f };
            foreach (UITextMatch match in found)
                MarkMinimapLine(list, map, mapScroll, first, last, match.Line, lit);
        }

        foreach (UICodeDiagnostic diagnostic in diagnostics)
        {
            if (!diagnostic.Hint)
                MarkMinimapLine(list, map, mapScroll, first, last, diagnostic.Line, (diagnostic.Warning ? skin.WarningColor : skin.ErrorColor) with { W = 0.6f });
        }
    }

    private void MarkMinimapLine(UIDrawList list, UIRect map, float mapScroll, int first, int last, int line, Vector4 colour)
    {
        if (line < 0 || line >= document.LineCount)
            return;

        int index = RowOf(line, 0);
        if (index < first || index > last)
            return;

        float top = map.Max.Y - (index * MINIMAP_ROW - mapScroll);
        list.Rect(new UIRect(new Vector2(map.Max.X - 6.0f, top - MINIMAP_ROW), new Vector2(map.Max.X, top)), colour);
    }

    /// <summary>Helper method for a press on the minimap, the slider taken hold of, or the code gone to there and then held. False off it.</summary>
    private bool PressMinimap(Vector2 point)
    {
        if (!Minimap || paintedSkin is not { } skin || rows.Count == 0)
            return false;

        UIRect map = MinimapArea(skin, paintedScale);
        if (!map.Contains(point))
            return false;

        float lineHeight = FontOf(skin).LineHeight * paintedScale;
        float down = SliderDown(map, lineHeight, scrollY);
        float height = MathF.Max(MINIMAP_ROW * 2.0f, map.Height / lineHeight * MINIMAP_ROW);
        float pointerDown = map.Max.Y - point.Y;

        // Off the slider, the row under the pointer is brought to the middle and the slider is held by its middle
        if (pointerDown < down || pointerDown > down + height)
        {
            int row = Math.Clamp((int)((pointerDown + MinimapScroll(map, lineHeight)) / MINIMAP_ROW), 0, rows.Count - 1);
            scrollGoal = null;
            scrollY = row * lineHeight - TextArea(skin, paintedScale).Height * 0.5f;
            ClampScroll(skin, paintedScale);

            // Held by its middle from here, a jump in a long file moves the minimap under the pointer as well
            minimapGrab = height * 0.5f;
            draggingMinimap = true;
            return true;
        }

        minimapGrab = pointerDown - down;
        draggingMinimap = true;
        return true;
    }

    /// <summary>Helper method to follow the slider being dragged, the code scrolled to where it is.</summary>
    private void DragMinimap(Vector2 point)
    {
        if (paintedSkin is not { } skin)
            return;

        UIRect map = MinimapArea(skin, paintedScale);
        float lineHeight = FontOf(skin).LineHeight * paintedScale;
        float most = MathF.Max(1.0f, rows.Count * lineHeight - map.Height * 0.5f);

        // The slider's place goes along with the scroll in a straight line, so where it's wanted gives the scroll
        float wanted = map.Max.Y - point.Y - minimapGrab;
        float atBottom = SliderDown(map, lineHeight, most);
        scrollGoal = null;
        scrollY = atBottom <= 0.0f ? 0.0f : Math.Clamp(wanted / atBottom, 0.0f, 1.0f) * most;
    }
}
