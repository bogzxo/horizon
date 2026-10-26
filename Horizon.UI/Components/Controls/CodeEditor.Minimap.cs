using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The minimap, the whole of the code drawn small down the right between the text and the scroll bar, VS Code's and
// Sublime's. Every row is a strip a couple of pixels tall with its words as little bars in their colours, the part in
// sight under a lighter slider that's dragged to scroll, a click anywhere else goes there. Down its left edge the bars of
// the lines that changed (LineChanges), and over it what was found and what's wrong, so a long file's busy places show
// at a glance. Taller than the view, it scrolls along with the code so the slider never leaves it. A file that's all in
// sight has nothing to scroll, it gets its rows and no slider.
public partial class CodeEditor
{
    // How wide it is, and how big a character and a row are drawn in it, in the editor's units
    private const float MINIMAP_WIDTH = 96.0f, MINIMAP_CHAR = 1.0f, MINIMAP_ROW = 2.0f;

    // The most characters of a row drawn, past that it runs off the edge like it would anyway
    private const int MINIMAP_COLUMNS = 92;

    private const int PART_MINIMAP = -10;

    /// <summary>Whether the code is drawn small down the right, a slider over what's in sight. Off unless the program turns it on.</summary>
    public bool Minimap { get; set; }

    /// <summary>How many of the screen's pixels tall a row of the minimap was last drawn, a whole number at any scale of the UI.</summary>
    public int MinimapRowPixels { get; private set; } = 2;

    /// <summary>The top of the minimap as it was last drawn, in the screen's pixels up from its bottom.</summary>
    public float MinimapTop { get; private set; }

    /// <summary>The top of its slider as it was last drawn, in the screen's pixels, NaN when there was no slider.</summary>
    public float MinimapSliderTop { get; private set; } = float.NaN;

    /// <summary>How far the minimap itself was scrolled when it was last drawn, in the screen's pixels.</summary>
    public float MinimapScrollPixels { get; private set; }

    /// <summary>The tops of the rows it last drew, in the screen's pixels, the first one in sight first.</summary>
    public IReadOnlyList<float> MinimapRowTops => minimapRowTops;

    private readonly List<float> minimapRowTops = [];

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

    // How many of the screen's pixels a unit of the editor is, 1.11 at a UI scale of 111%
    private float PixelsPerUnit => Module is { } module ? MathF.Max(0.01f, module.ToWorld(Vector2.UnitY).Y - module.ToWorld(Vector2.Zero).Y) : 1.0f;

    // How tall a row of the minimap is in units, a whole number of pixels, two at 100% and 111% and three at 150%
    private float MinimapRow()
    {
        float pixels = PixelsPerUnit;
        return MathF.Max(1.0f, MathF.Round(MINIMAP_ROW * pixels)) / pixels;
    }

    // A place moved onto the nearest corner of the screen's pixels
    private Vector2 OnPixel(Vector2 point) => Module is { } module ? module.ToLocal(Vector2.Round(module.ToWorld(point))) : Vector2.Round(point);

    // A height in units as it is on the screen, in pixels up from its bottom
    private float ScreenY(float y) => Module is { } module ? module.ToWorld(new Vector2(0.0f, y)).Y : y;

    /// <summary>
    /// Helper method for how far the minimap itself is scrolled, as much of it as the code is, so that the slider is at the
    /// top with the code at the top and at the bottom with the code at the bottom.
    /// </summary>
    private float MinimapScroll(UIRect map, float lineHeight, float row)
    {
        float content = rows.Count * row;
        if (content <= map.Height)
            return 0.0f;

        float most = MathF.Max(1.0f, rows.Count * lineHeight - map.Height * 0.5f);
        return (content - map.Height) * Math.Clamp(scrollY / most, 0.0f, 1.0f);
    }

    /// <summary>Helper method for the slider's top, down from the minimap's top, for a scroll of the code.</summary>
    private float SliderDown(UIRect map, float lineHeight, float scroll, float row)
    {
        float content = rows.Count * row;
        float most = MathF.Max(1.0f, rows.Count * lineHeight - map.Height * 0.5f);
        float mapScroll = content <= map.Height ? 0.0f : (content - map.Height) * Math.Clamp(scroll / most, 0.0f, 1.0f);
        return scroll / lineHeight * row - mapScroll;
    }

    private void PaintMinimap(UIDrawList list, UISkin skin, float scale, UISyntaxColors colors, float lineHeight)
    {
        if (!Minimap || rows.Count == 0)
            return;

        // Everything of it sits on whole pixels of the screen and a row is a whole number of them tall. A row was two
        // units, 2.22 pixels at 111%, and slid along by a bit of a pixel every tick as the code scrolled each one covered
        // other pixels from one frame to the next, the whole map flashing as it went and as the window was resized
        float pixels = PixelsPerUnit, pixel = 1.0f / pixels;
        float row = MinimapRow();
        float character = MathF.Max(1.0f, MathF.Round(MINIMAP_CHAR * pixels)) * pixel;
        UIRect area = MinimapArea(skin, scale);
        var map = new UIRect(OnPixel(area.Min), OnPixel(area.Max));
        float top = map.Max.Y;

        // The scroll goes a whole row at a time, so every row stays on the pixels it's on and just stands for another line
        int scrolledRows = (int)MathF.Round(MinimapScroll(map, lineHeight, row) / row);
        int first = Math.Clamp(scrolledRows, 0, rows.Count - 1);
        int last = Math.Min(rows.Count - 1, first + (int)(map.Height / row) + 1);

        // All of the code in sight, there's nothing to scroll and no slider to show where
        bool fits = rows.Count * lineHeight <= map.Height;

        MinimapRowPixels = (int)MathF.Round(row * pixels);
        MinimapTop = ScreenY(top);
        MinimapScrollPixels = scrolledRows * MinimapRowPixels;
        minimapRowTops.Clear();

        // Its rows stand for other lines from one tick to the next as it scrolls, nothing of it is blended between ticks
        list.BeginPart(PART_MINIMAP);
        list.BeginSteps();
        list.PushClip(map);
        if (!fits)
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

        // A gap of a pixel between rows once they're three or more tall, two pixels with one of them gap is a dotted mess
        float gap = MinimapRowPixels >= 3 ? pixel : 0.0f;
        float left = OnPixel(new Vector2(map.Min.X + 4.0f, 0.0f)).X;
        for (int index = first; index <= last; index++)
        {
            CodeRow shown = rows[index];
            float rowTop = top - (index - scrolledRows) * row;
            minimapRowTops.Add(ScreenY(rowTop));
            string text = document.LineAt(shown.Line);
            MinimapWord[] words = WordsOf(shown.Line, text, colors);

            // A wrapped row starts its columns again where it starts in the line
            int rowColumn = shown.Start == 0 ? 0 : Column(text, 0, shown.Start);
            foreach (MinimapWord word in words)
            {
                if (word.End <= shown.Start || word.Start >= shown.End)
                    continue;

                int from = Math.Max(word.StartColumn, rowColumn) - rowColumn;
                int to = Math.Min(MINIMAP_COLUMNS, Math.Min(word.EndColumn, shown.End >= text.Length ? word.EndColumn : Column(text, 0, shown.End)) - rowColumn);
                if (to > from)
                    list.Rect(new UIRect(new Vector2(left + from * character, rowTop - row + gap), new Vector2(left + to * character, rowTop)), word.Colour);
            }
        }

        PaintMinimapMarks(list, skin, map, row, scrolledRows, first, last);

        // The slider over what's in sight, lighter under the pointer and while it's held. Its top on a pixel and its
        // height a whole number of them, so it doesn't swim over the rows under it
        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : Vector2.Zero;
        bool lit = draggingMinimap || (IsHovered && map.Contains(pointer));
        MinimapSliderTop = float.NaN;
        if (!fits)
        {
            float height = MathF.Max(row * 2.0f, map.Height / lineHeight * row);
            float sliderTop = OnPixel(new Vector2(0.0f, top - SliderDown(map, lineHeight, scrollY, row))).Y;
            var slider = new UIRect(new Vector2(map.Min.X, sliderTop - MathF.Round(height * pixels) * pixel), new Vector2(map.Max.X, sliderTop));
            list.Rect(slider, skin.TextColor with { W = lit ? 0.2f : 0.12f });
            MinimapSliderTop = ScreenY(sliderTop);
        }

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
    private void PaintMinimapMarks(UIDrawList list, UISkin skin, UIRect map, float row, int scrolledRows, int first, int last)
    {
        float pixel = 1.0f / PixelsPerUnit;
        for (int index = first; index <= last; index++)
        {
            CodeRow shown = rows[index];
            float top = map.Max.Y - (index - scrolledRows) * row;

            if (changedLines.TryGetValue(shown.Line, out UILineChangeKind kind))
                list.Rect(new UIRect(new Vector2(map.Min.X, top - row), new Vector2(map.Min.X + 2.0f, top)), ChangeColour(kind, skin));
            if (shown.Start == 0 && deletedBefore.Contains(shown.Line))
                list.Rect(new UIRect(new Vector2(map.Min.X, top - pixel), new Vector2(map.Min.X + 4.0f, top + pixel)), skin.ErrorColor);

            // A TODO or a FIXME, a mark down the right edge in its colour
            if (HighlightTodos && shown.Start == 0 && TodoKindOf(shown.Line) is var todo and >= 0)
                list.Rect(new UIRect(new Vector2(map.Max.X - 6.0f, top - row), new Vector2(map.Max.X, top)), todo > 0 ? skin.ErrorColor : skin.WarningColor);

            if (shown.Line == document.Caret.Line && shown.Start == 0)
                list.Rect(new UIRect(new Vector2(map.Min.X, top - row), new Vector2(map.Max.X, top)), skin.TextColor with { W = 0.22f });
        }

        // What was found, and what's wrong, across the rows they're on
        if (findQuery.Length > 0)
        {
            Vector4 lit = skin.HighlightColor with { W = 0.5f };
            foreach (UITextMatch match in found)
                MarkMinimapLine(list, map, row, scrolledRows, first, last, match.Line, lit);
        }

        foreach (UICodeDiagnostic diagnostic in diagnostics)
        {
            if (!diagnostic.Hint)
                MarkMinimapLine(list, map, row, scrolledRows, first, last, diagnostic.Line, (diagnostic.Warning ? skin.WarningColor : skin.ErrorColor) with { W = 0.6f });
        }
    }

    private void MarkMinimapLine(UIDrawList list, UIRect map, float row, int scrolledRows, int first, int last, int line, Vector4 colour)
    {
        if (line < 0 || line >= document.LineCount)
            return;

        int index = RowOf(line, 0);
        if (index < first || index > last)
            return;

        float top = map.Max.Y - (index - scrolledRows) * row;
        list.Rect(new UIRect(new Vector2(map.Max.X - 6.0f, top - row), new Vector2(map.Max.X, top)), colour);
    }

    /// <summary>Helper method for a press on the minimap, the slider taken hold of, or the code gone to there and then held. False off it.</summary>
    private bool PressMinimap(Vector2 point)
    {
        if (!Minimap || paintedSkin is not { } skin || rows.Count == 0)
            return false;

        UIRect map = MinimapArea(skin, paintedScale);
        if (!map.Contains(point))
            return false;

        float row = MinimapRow();
        float lineHeight = FontOf(skin).LineHeight * paintedScale;
        float down = SliderDown(map, lineHeight, scrollY, row);
        float height = MathF.Max(row * 2.0f, map.Height / lineHeight * row);
        float pointerDown = map.Max.Y - point.Y;

        // Off the slider, the row under the pointer is brought to the middle and the slider is held by its middle
        if (pointerDown < down || pointerDown > down + height)
        {
            int at = Math.Clamp((int)((pointerDown + MinimapScroll(map, lineHeight, row)) / row), 0, rows.Count - 1);
            scrollGoal = null;
            scrollY = at * lineHeight - TextArea(skin, paintedScale).Height * 0.5f;
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
        float atBottom = SliderDown(map, lineHeight, most, MinimapRow());
        scrollGoal = null;
        scrollY = atBottom <= 0.0f ? 0.0f : Math.Clamp(wanted / atBottom, 0.0f, 1.0f) * most;
    }
}
