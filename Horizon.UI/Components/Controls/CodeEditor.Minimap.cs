using System.Numerics;
using System.Runtime.InteropServices;

using Horizon.UI.Drawing;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// The minimap, the whole of the code drawn small down the right between the text and the scroll bar, VS Code's and
// Sublime's. Every row is a strip a couple of pixels tall with its words as little bars in their colours, the part in
// sight under a lighter slider that's dragged to scroll, a click anywhere else goes there. Down its left edge the bars of
// the lines that changed (LineChanges) and down its right the TODOs. What was found and what's wrong are ticked on the
// scroll bar next to it (CodeEditor.Bar), so a long file's busy places show at a glance. Taller than the view, it
// scrolls along with the code so the slider never leaves it. A file that's all in sight has nothing to scroll, it gets
// its rows and no slider.
public partial class CodeEditor
{
    // How wide it is in the editor's units, a character and a row are UIMinimap's
    private const float MINIMAP_WIDTH = 96.0f;

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

    /// <summary>A precomputed minimap bar quad and its faded colour, cached in a flat contiguous list.</summary>
    private readonly record struct MinimapRun(UIRect Rect, Vector4 Colour);

    /// <summary>A precomputed TODO or FIXME mark on a visible minimap row.</summary>
    private readonly record struct MinimapTodoMark(int RowIndex, bool Urgent);

    private readonly record struct MinimapRowSlice(int StartRun, int Count);

    private readonly record struct MinimapCacheKey(
        int DocumentVersion,
        int ColouredVersion,
        List<SyntaxSpan>[]? Spans,
        UISyntaxColors? Colors,
        int LanguagesVersion,
        int ColorsFingerprint,
        bool Highlight,
        bool HighlightTodos,
        int RowsCount,
        int FoldsChanged,
        bool WordWrap,
        float RowsWidth,
        float RowsScale,
        int RowsShapes,
        int RowsScrolled,
        int RowsInlays,
        int RowsLenses,
        int? RowsPredicted,
        int RowsBlocks,
        int First,
        int Last,
        int ScrolledRows,
        float Left,
        float Top,
        float Row,
        float Gap,
        float Character,
        float Pixels,
        float ScreenTop)
    {
        public bool MatchesBase(in MinimapCacheKey other) =>
            DocumentVersion == other.DocumentVersion
            && ColouredVersion == other.ColouredVersion
            && ReferenceEquals(Spans, other.Spans)
            && ReferenceEquals(Colors, other.Colors)
            && LanguagesVersion == other.LanguagesVersion
            && ColorsFingerprint == other.ColorsFingerprint
            && Highlight == other.Highlight
            && HighlightTodos == other.HighlightTodos
            && RowsCount == other.RowsCount
            && FoldsChanged == other.FoldsChanged
            && WordWrap == other.WordWrap
            && RowsWidth == other.RowsWidth
            && RowsScale == other.RowsScale
            && RowsShapes == other.RowsShapes
            && RowsScrolled == other.RowsScrolled
            && RowsInlays == other.RowsInlays
            && RowsLenses == other.RowsLenses
            && RowsPredicted == other.RowsPredicted
            && RowsBlocks == other.RowsBlocks
            && Left == other.Left
            && Top == other.Top
            && Row == other.Row
            && Gap == other.Gap
            && Character == other.Character
            && Pixels == other.Pixels
            && ScreenTop == other.ScreenTop;
    }

    private List<MinimapRun> minimapRuns = [];
    private List<MinimapRun> minimapRunsScratch = [];
    private List<MinimapRowSlice> minimapRowSlices = [];
    private List<MinimapRowSlice> minimapSlicesScratch = [];
    private List<MinimapTodoMark> minimapTodoMarks = [];
    private List<MinimapTodoMark> minimapTodoScratch = [];
    private readonly List<MinimapWord> minimapWordScratch = [];
    private MinimapCacheKey minimapCacheKey;
    private bool minimapCacheValid;
    private bool minimapLastWordsUncoloured;

    internal int MinimapCacheHits { get; private set; }
    internal int MinimapIncrementalHits { get; private set; }
    internal int MinimapCacheMisses { get; private set; }
    internal void ClearMinimapCache()
    {
        minimapCacheValid = false;
        minimapSlotGen++;
    }

    // Small direct-mapped per-line reference cache so typing/scrolling avoids hashing 300+ unchanged line strings every paint.
    private struct MinimapSlot
    {
        public int Generation;
        public string? Text;
        public MinimapWord[]? Words;
        public sbyte Todo;
        public bool HasTodo;
    }

    private const int MINIMAP_SLOTS = 512;
    private readonly MinimapSlot[] minimapSlots = new MinimapSlot[MINIMAP_SLOTS];
    private int minimapSlotGen = 1;

    // Where the slider was taken from, as a share of the slider's height, while it's dragged
    private bool draggingMinimap;
    private float minimapGrab;

    // How wide an editor has to be for the minimap, five of its widths. At 200% beside the side bar and the values the
    // editor was a few hundred units across, and the minimap took a third of it from the code
    private const float MINIMAP_ROOM = MINIMAP_WIDTH * 5.0f;

    /// <summary>Whether the minimap is drawn, on (<see cref="Minimap"/>) and the editor wide enough for the code beside it.</summary>
    public bool MinimapShown => Minimap && Bounds.Width >= MINIMAP_ROOM;

    private float MinimapWidth => MinimapShown ? MINIMAP_WIDTH : 0.0f;

    /// <summary>Helper method for where the minimap is, between the text and the scroll bar.</summary>
    private UIRect MinimapArea(UISkin skin, float scale)
    {
        UIRect area = TextArea(skin, scale);
        return new UIRect(new Vector2(area.Max.X, area.Min.Y), new Vector2(area.Max.X + MinimapWidth, area.Max.Y));
    }

    // How many of the screen's pixels a unit of the editor is, 1.11 at a UI scale of 111%
    private float PixelsPerUnit => UIMinimap.PixelsPerUnit(Module);

    // How tall a row of the minimap is in units, a whole number of pixels, two at 100% and 111% and three at 150%
    private float MinimapRow() => UIMinimap.Row(PixelsPerUnit);

    // A place moved onto the nearest corner of the screen's pixels
    private Vector2 OnPixel(Vector2 point) => UIMinimap.OnPixel(Module, point);

    // A height in units as it is on the screen, in pixels up from its bottom
    private float ScreenY(float y) => UIMinimap.ScreenY(Module, y);

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
        if (!MinimapShown || rows.Count == 0)
            return;

        // Everything of it sits on whole pixels of the screen and a row is a whole number of them tall. A row was two
        // units, 2.22 pixels at 111%, and slid along by a bit of a pixel every tick as the code scrolled each one covered
        // other pixels from one frame to the next, the whole map flashing as it went and as the window was resized
        float pixels = PixelsPerUnit;
        float row = MinimapRow();
        float character = UIMinimap.Whole(UIMinimap.CHARACTER, pixels);
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
        float screenTop = ScreenY(top);
        MinimapTop = screenTop;
        MinimapScrollPixels = scrolledRows * MinimapRowPixels;

        // Its rows stand for other lines from one tick to the next as it scrolls, nothing of it is blended between ticks
        list.BeginPart(PART_MINIMAP);
        list.PushClip(map);
        if (!fits)
            list.Rect(map, skin.TextColor with { W = 0.025f });

        // The words of a line are worked out once and kept by its text, typing into a big file paints a few hundred rows
        // of these every tick and going through every span of every row again was most of a tick. Kept across colourings,
        // which every key starts, and let go of when the theme's colours change
        if (!ReferenceEquals(minimapColoursFor, colors) || minimapHighlightFor != Highlight)
        {
            minimapWords.Clear();
            minimapTodos.Clear();
            minimapColoursFor = colors;
            minimapHighlightFor = Highlight;
            minimapWordsLast = null;
            minimapCacheValid = false;
            minimapSlotGen++;
        }

        // A gap of a pixel between rows once they're three or more tall
        float gap = UIMinimap.Gap(row, pixels);
        float left = OnPixel(new Vector2(map.Min.X + 4.0f, 0.0f)).X;

        var key = new MinimapCacheKey(
            document.Version,
            coloured,
            spans,
            colors,
            CodeLanguages.Version,
            colors.Fingerprint(),
            Highlight,
            HighlightTodos,
            rows.Count,
            rowsFor.Folds,
            rowsFor.Wrap,
            rowsFor.Width,
            rowsFor.Scale,
            rowsFor.Shapes,
            rowsFor.Scrolled,
            rowsFor.Inlays,
            rowsFor.Lenses,
            rowsFor.Predicted,
            rowsFor.Blocks,
            first,
            last,
            scrolledRows,
            left,
            top,
            row,
            gap,
            character,
            pixels,
            screenTop);

        if (minimapCacheValid && !longReading && minimapCacheKey == key)
        {
            MinimapCacheHits++;
        }
        else if (TryShiftMinimapScroll(in key, colors, first, last, scrolledRows, left, top, row, gap, character))
        {
            MinimapIncrementalHits++;
        }
        else
        {
            MinimapCacheMisses++;
            minimapRowTops.Clear();
            minimapRowSlices.Clear();
            minimapRuns.Clear();
            minimapTodoMarks.Clear();

            bool cacheable = !longReading;
            int countedLine = -1, countedAt = 0, countedColumn = 0;
            for (int index = first; index <= last; index++)
            {
                float rowTop = top - (index - scrolledRows) * row;
                minimapRowTops.Add(ScreenY(rowTop));

                int runStart = minimapRuns.Count;
                cacheable &= EmitMinimapRow(index, rowTop, left, row, gap, character, colors, minimapRuns, minimapTodoMarks, ref countedLine, ref countedAt, ref countedColumn);
                minimapRowSlices.Add(new MinimapRowSlice(runStart, minimapRuns.Count - runStart));
            }

            minimapCacheKey = key;
            minimapCacheValid = cacheable && !longReading;
        }

        foreach (ref readonly MinimapRun run in CollectionsMarshal.AsSpan(minimapRuns))
            list.Rect(run.Rect, run.Colour);

        PaintMinimapMarks(list, skin, map, row, scrolledRows, first, last);

        // The slider over what's in sight, lighter under the pointer and while it's held
        Vector2 pointer = Module is { } module ? module.ToLocal(module.Compositor.Pointer.Position) : Vector2.Zero;
        bool lit = draggingMinimap || (IsHovered && map.Contains(pointer));
        MinimapSliderTop = float.NaN;
        if (!fits)
        {
            float height = MathF.Max(row * 2.0f, map.Height / lineHeight * row);
            float sliderTop = UIMinimap.Slider(list, Module, map, top - SliderDown(map, lineHeight, scrollY, row), height, skin.TextColor, lit);
            MinimapSliderTop = ScreenY(sliderTop);
        }

        list.PopClip();
    }

    private bool EmitMinimapRow(
        int index,
        float rowTop,
        float left,
        float row,
        float gap,
        float character,
        UISyntaxColors colors,
        List<MinimapRun> runsOut,
        List<MinimapTodoMark> todosOut,
        ref int countedLine,
        ref int countedAt,
        ref int countedColumn)
    {
        CodeRow shown = rows[index];
        if (shown.Virtual)
            return true;

        string text = document.LineAt(shown.Line);
        MinimapWord[] words = WordsOf(shown.Line, text, colors);
        bool cacheable = !minimapLastWordsUncoloured;

        // A wrapped row starts its columns again where it starts in the line.
        if (countedLine != shown.Line || countedAt > shown.Start)
            (countedLine, countedAt, countedColumn) = (shown.Line, 0, 0);
        int rowColumn = countedColumn + Column(text, countedAt, shown.Start);
        int endColumn = shown.End >= text.Length ? int.MaxValue : rowColumn + Column(text, shown.Start, shown.End);
        (countedAt, countedColumn) = (Math.Min(shown.End, text.Length), endColumn == int.MaxValue ? 0 : endColumn);
        if (endColumn == int.MaxValue)
            countedLine = -1;

        for (int w = FirstWordTo(words, shown.Start); w < words.Length && words[w].Start < shown.End; w++)
        {
            MinimapWord word = words[w];
            int from = Math.Max(word.StartColumn, rowColumn) - rowColumn;
            if (from >= MINIMAP_COLUMNS)
                break;
            int to = Math.Min(Math.Min(word.EndColumn, endColumn) - rowColumn, MINIMAP_COLUMNS);
            if (to > from)
            {
                var rect = new UIRect(
                    new Vector2(left + from * character, rowTop - row + gap),
                    new Vector2(left + to * character, rowTop));
                runsOut.Add(new MinimapRun(rect, word.Colour with { W = word.Colour.W * 0.42f }));
            }
        }

        if (HighlightTodos && shown.Start == 0 && TodoKindOf(shown.Line) is var todo and >= 0)
            todosOut.Add(new MinimapTodoMark(index, todo > 0));

        return cacheable;
    }

    private bool TryShiftMinimapScroll(
        in MinimapCacheKey key,
        UISyntaxColors colors,
        int first,
        int last,
        int scrolledRows,
        float left,
        float top,
        float row,
        float gap,
        float character)
    {
        if (!minimapCacheValid || longReading || !minimapCacheKey.MatchesBase(in key))
            return false;

        int prevFirst = minimapCacheKey.First;
        int prevLast = minimapCacheKey.Last;
        if (minimapRowSlices.Count != prevLast - prevFirst + 1)
            return false;

        int overlapFirst = Math.Max(first, prevFirst);
        int overlapLast = Math.Min(last, prevLast);
        if (overlapFirst > overlapLast)
            return false;

        float dy = (scrolledRows - minimapCacheKey.ScrolledRows) * row;
        minimapRowTops.Clear();
        minimapRunsScratch.Clear();
        minimapSlicesScratch.Clear();
        minimapTodoScratch.Clear();

        for (int index = first; index <= last; index++)
            minimapRowTops.Add(ScreenY(top - (index - scrolledRows) * row));

        bool cacheable = true;
        int countedLine = -1, countedAt = 0, countedColumn = 0;

        // Newly revealed rows above the previous window (ascending order for PaintMinimapMarks).
        for (int index = first; index < overlapFirst; index++)
        {
            float rowTop = top - (index - scrolledRows) * row;
            int runStart = minimapRunsScratch.Count;
            cacheable &= EmitMinimapRow(index, rowTop, left, row, gap, character, colors, minimapRunsScratch, minimapTodoScratch, ref countedLine, ref countedAt, ref countedColumn);
            minimapSlicesScratch.Add(new MinimapRowSlice(runStart, minimapRunsScratch.Count - runStart));
        }

        // Overlapping rows retain their cached colour runs translated by dy.
        ReadOnlySpan<MinimapRun> oldRuns = CollectionsMarshal.AsSpan(minimapRuns);
        ReadOnlySpan<MinimapRowSlice> oldSlices = CollectionsMarshal.AsSpan(minimapRowSlices);
        for (int index = overlapFirst; index <= overlapLast; index++)
        {
            MinimapRowSlice prevSlice = oldSlices[index - prevFirst];
            int newStart = minimapRunsScratch.Count;
            for (int r = 0; r < prevSlice.Count; r++)
            {
                MinimapRun run = oldRuns[prevSlice.StartRun + r];
                if (dy != 0.0f)
                {
                    run = run with
                    {
                        Rect = new UIRect(
                            new Vector2(run.Rect.Min.X, run.Rect.Min.Y + dy),
                            new Vector2(run.Rect.Max.X, run.Rect.Max.Y + dy))
                    };
                }
                minimapRunsScratch.Add(run);
            }
            minimapSlicesScratch.Add(new MinimapRowSlice(newStart, prevSlice.Count));
        }

        ReadOnlySpan<MinimapTodoMark> oldTodos = CollectionsMarshal.AsSpan(minimapTodoMarks);
        for (int t = 0; t < oldTodos.Length; t++)
        {
            MinimapTodoMark mark = oldTodos[t];
            if (mark.RowIndex >= overlapFirst && mark.RowIndex <= overlapLast)
                minimapTodoScratch.Add(mark);
        }

        // Newly revealed rows below the previous window.
        countedLine = -1;
        countedAt = 0;
        countedColumn = 0;
        for (int index = overlapLast + 1; index <= last; index++)
        {
            float rowTop = top - (index - scrolledRows) * row;
            int runStart = minimapRunsScratch.Count;
            cacheable &= EmitMinimapRow(index, rowTop, left, row, gap, character, colors, minimapRunsScratch, minimapTodoScratch, ref countedLine, ref countedAt, ref countedColumn);
            minimapSlicesScratch.Add(new MinimapRowSlice(runStart, minimapRunsScratch.Count - runStart));
        }

        (minimapRuns, minimapRunsScratch) = (minimapRunsScratch, minimapRuns);
        (minimapRowSlices, minimapSlicesScratch) = (minimapSlicesScratch, minimapRowSlices);
        (minimapTodoMarks, minimapTodoScratch) = (minimapTodoScratch, minimapTodoMarks);

        minimapCacheKey = key;
        minimapCacheValid = cacheable && !longReading;
        return true;
    }


    /// <summary>A word of a line as the minimap draws it, where it starts and ends in the line and its colour.</summary>
    private readonly record struct MinimapWord(int Start, int End, int StartColumn, int EndColumn, Vector4 Colour);

    // The words of the lines drawn, by the line's text, for the colours they were worked out with
    private readonly Dictionary<string, MinimapWord[]> minimapWords = new(StringComparer.Ordinal);

    // Whether a line has a TODO (0) or a FIXME (1) or neither (-1), by its text, kept like its words
    private readonly Dictionary<string, sbyte> minimapTodos = new(StringComparer.Ordinal);
    private object? minimapColoursFor;
    private bool minimapHighlightFor = true;

    /// <summary>
    /// Helper method for the words of a line, every run of what isn't white space one bar, the colour of its first piece
    /// that isn't punctuation. A word of five tokens (thing_1: {) is one bar, not five.
    /// </summary>
    private MinimapWord[] WordsOf(int line, string text, UISyntaxColors colors)
    {
        minimapLastWordsUncoloured = false;

        // The line of the row before again, every row of a wrapped line asks for it, by its text it was hashed whole each
        // time. Words not kept (the line isn't coloured yet) are only for the rows of this paint
        if (ReferenceEquals(text, minimapWordsText) && minimapWordsLast is { } last && (minimapWordsKept || minimapWordsPaint == flowPaint))
            return last;

        ref MinimapSlot slot = ref minimapSlots[line & (MINIMAP_SLOTS - 1)];
        if (slot.Generation == minimapSlotGen && ReferenceEquals(slot.Text, text) && slot.Words is { } slotted)
        {
            (minimapWordsText, minimapWordsLast, minimapWordsKept) = (text, slotted, true);
            return slotted;
        }

        if (minimapWords.TryGetValue(text, out MinimapWord[]? known))
        {
            slot.Generation = minimapSlotGen;
            slot.Text = text;
            slot.Words = known;
            slot.HasTodo = false;
            (minimapWordsText, minimapWordsLast, minimapWordsKept) = (text, known, true);
            return known;
        }

        // Typed into, the line's colours are the ones worked out for it alone. Without them every row of a long line went
        // through all of it again every paint until the colouring came back, a second a paint for a fifth of a megabyte.
        // Those are only for this paint, the colouring may yet start the line otherwise
        List<SyntaxSpan>? coloured = ColoursOf(line, text), spans = coloured ?? FreshColours(line, text);
        minimapLastWordsUncoloured = coloured is null && spans is null && Highlight && text.Length > 0;
        minimapWordScratch.Clear();
        int at = 0, column = 0, span = 0;
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
                minimapWordScratch.Add(new MinimapWord(start, at, startColumn, column, Faded(KindAt(spans, ref span, start, at), colors)));
        }

        MinimapWord[] made = minimapWordScratch.Count == 0 ? [] : [.. minimapWordScratch];

        // Lines not coloured yet aren't kept, they'd stay plain once they are
        if (coloured is not null)
        {
            if (minimapWords.Count > 20000)
                minimapWords.Clear();
            minimapWords[text] = made;
            slot.Generation = minimapSlotGen;
            slot.Text = text;
            slot.Words = made;
            slot.HasTodo = false;
        }

        (minimapWordsText, minimapWordsLast, minimapWordsKept, minimapWordsPaint) = (text, made, coloured is not null, flowPaint);
        return made;
    }

    // The words WordsOf last gave, the very string they're of, whether they're kept and the paint they were worked out in
    private string? minimapWordsText;
    private MinimapWord[]? minimapWordsLast;
    private bool minimapWordsKept;
    private int minimapWordsPaint;

    // Helper method for the first of a line's words that ends after a place in it
    private static int FirstWordTo(MinimapWord[] words, int at)
    {
        int low = 0, high = words.Length;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (words[middle].End <= at) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    // The kind of the first piece of a word that isn't punctuation, a name for one that's all punctuation or not coloured.
    // The words are asked for in order, so the spans before the word's are stepped over once and not for every word
    private static SyntaxKind KindAt(List<SyntaxSpan>? spans, ref int from, int start, int end)
    {
        if (spans is null)
            return SyntaxKind.Name;

        while (from < spans.Count && spans[from].Start + spans[from].Length <= start)
            from++;

        SyntaxKind? punctuation = null;
        for (int i = from; i < spans.Count; i++)
        {
            SyntaxSpan span = spans[i];
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
        ref MinimapSlot slot = ref minimapSlots[line & (MINIMAP_SLOTS - 1)];
        if (slot.Generation == minimapSlotGen && ReferenceEquals(slot.Text, text) && slot.HasTodo)
            return slot.Todo;

        if (minimapTodos.TryGetValue(text, out sbyte known))
        {
            if (slot.Generation == minimapSlotGen && ReferenceEquals(slot.Text, text))
            {
                slot.Todo = known;
                slot.HasTodo = true;
            }
            return known;
        }

        sbyte kind = TodoKindOn(line, text);

        // A line not coloured yet isn't kept, it can't be told whether its TODO is in a comment
        if (ColoursOf(line, text) is not null)
        {
            if (minimapTodos.Count > 20000)
                minimapTodos.Clear();
            minimapTodos[text] = kind;
            if (slot.Generation == minimapSlotGen && ReferenceEquals(slot.Text, text))
            {
                slot.Todo = kind;
                slot.HasTodo = true;
            }
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


    /// <summary>Helper method for the changes down the left edge, the TODOs down the right and the caret's line across.</summary>
    private void PaintMinimapMarks(UIDrawList list, UISkin skin, UIRect map, float row, int scrolledRows, int first, int last)
    {
        float pixel = 1.0f / PixelsPerUnit;
        ReadOnlySpan<MinimapTodoMark> todos = CollectionsMarshal.AsSpan(minimapTodoMarks);

        if (changedLines.Count == 0 && deletedBefore.Count == 0)
        {
            int caretRow = RowOf(document.Caret.Line, 0);
            bool hasCaret = caretRow >= first && caretRow <= last
                && !rows[caretRow].Virtual && rows[caretRow].Line == document.Caret.Line && rows[caretRow].Start == 0;
            bool caretPainted = false;

            for (int i = 0; i < todos.Length; i++)
            {
                MinimapTodoMark mark = todos[i];
                if (hasCaret && !caretPainted && mark.RowIndex > caretRow)
                {
                    float caretTop = map.Max.Y - (caretRow - scrolledRows) * row;
                    list.Rect(new UIRect(new Vector2(map.Min.X, caretTop - row), new Vector2(map.Max.X, caretTop)), skin.TextColor with { W = 0.22f });
                    caretPainted = true;
                }

                float top = map.Max.Y - (mark.RowIndex - scrolledRows) * row;
                list.Rect(new UIRect(new Vector2(map.Max.X - 6.0f, top - row), new Vector2(map.Max.X, top)), mark.Urgent ? skin.ErrorColor : skin.WarningColor);
            }

            if (hasCaret && !caretPainted)
            {
                float caretTop = map.Max.Y - (caretRow - scrolledRows) * row;
                list.Rect(new UIRect(new Vector2(map.Min.X, caretTop - row), new Vector2(map.Max.X, caretTop)), skin.TextColor with { W = 0.22f });
            }
        }
        else
        {
            int todoCursor = 0;
            for (int index = first; index <= last; index++)
            {
                CodeRow shown = rows[index];
                float top = map.Max.Y - (index - scrolledRows) * row;
                if (shown.Virtual)
                    continue;

                if (changedLines.TryGetValue(shown.Line, out UILineChangeKind kind))
                    list.Rect(new UIRect(new Vector2(map.Min.X, top - row), new Vector2(map.Min.X + 2.0f, top)), ChangeColour(kind, skin));
                if (shown.Start == 0 && deletedBefore.Contains(shown.Line))
                    list.Rect(new UIRect(new Vector2(map.Min.X, top - pixel), new Vector2(map.Min.X + 4.0f, top + pixel)), skin.ErrorColor);

                while (todoCursor < todos.Length && todos[todoCursor].RowIndex < index)
                    todoCursor++;
                if (todoCursor < todos.Length && todos[todoCursor].RowIndex == index)
                {
                    list.Rect(new UIRect(new Vector2(map.Max.X - 6.0f, top - row), new Vector2(map.Max.X, top)), todos[todoCursor].Urgent ? skin.ErrorColor : skin.WarningColor);
                    todoCursor++;
                }

                if (shown.Line == document.Caret.Line && shown.Start == 0)
                    list.Rect(new UIRect(new Vector2(map.Min.X, top - row), new Vector2(map.Max.X, top)), skin.TextColor with { W = 0.22f });
            }
        }

        // A remote caret's line, a bar across the map in its device's colour, never thinner than three pixels
        for (int i = 0; i < RemoteCarets.Count; i++)
        {
            UICodeRemoteCaret remote = RemoteCarets[i];
            if ((uint)remote.Line >= document.LineCount)
                continue;
            int at = RowOf(remote.Line, Math.Clamp(remote.Column, 0, document.LineAt(remote.Line).Length));
            if (at < first || at > last)
                continue;
            float top = map.Max.Y - (at - scrolledRows) * row;
            float height = MathF.Max(row, 3.0f * pixel);
            list.Rect(new UIRect(new Vector2(map.Min.X, top - height), new Vector2(map.Max.X, top)), remote.Colour with { W = 0.85f });
        }

        // What was found and what's wrong are ticked on the scroll bar's track (PaintTicks), they ran into its thumb here
    }

    /// <summary>Helper method for a press on the minimap, the slider taken hold of, or the code gone to there and then held. False off it.</summary>
    private bool PressMinimap(Vector2 point)
    {
        if (!MinimapShown || paintedSkin is not { } skin || rows.Count == 0)
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
