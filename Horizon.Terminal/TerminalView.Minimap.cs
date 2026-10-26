using System.Numerics;

using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// The minimap down the right of a terminal, between its cells and its scroll bar, the code editor's for the scrollback.
// Every row is a strip a couple of pixels tall with its cells as little bars in the colours they're written in, the rows
// in sight under a lighter slider that's dragged to scroll, a click anywhere else brings that row to the middle. The
// prompts of the commands are marked down its left edge coloured by how they ended, and what a find found down its
// right. A row's bars are worked out once and kept on the row until it changes, so a terminal printing fast goes through
// the cells of the few rows at the bottom and not the thousands above them. A program with the whole screen (vim) gets
// its screen drawn small and no slider, it has no scrollback to go through.
public partial class TerminalView
{
    // How wide it is in the view's units, the most columns of a row it draws, and its part of the paint
    private const float MINIMAP_WIDTH = 80.0f, MINIMAP_GAP = 4.0f;
    private const int MINIMAP_COLUMNS = 240;
    private const int PART_MINIMAP = -6;

    // How see-through a row's text is in it, and a background that isn't the terminal's
    private const float MINIMAP_INK = 0.65f, MINIMAP_BACK = 0.4f;

    /// <summary>Whether the rows are drawn small down the right, a slider over the ones in sight. Off unless the program turns it on.</summary>
    public bool Minimap { get; set; }

    /// <summary>Where the minimap was last drawn, in the view's units, nothing while it's off.</summary>
    public UIRect MinimapBounds => Minimap ? minimapMap : default;

    /// <summary>How many rows the minimap drew last time, for a test.</summary>
    public int MinimapRowsDrawn { get; private set; }

    /// <summary>The first row of the whole (scrollback and screen) the minimap drew last time, for a test.</summary>
    public int MinimapFirstRow { get; private set; }

    // Where it is as the paint worked it out, and the same on whole pixels as it was drawn
    private UIRect minimapArea, minimapMap;

    // Its slider held, and where it was taken hold of, down from the slider's top
    private bool draggingMinimap;
    private float minimapGrab;

    // What the kept bars of the rows were worked out with, bumped when the colours change so every row is done again
    private int minimapGeneration;
    private TerminalPalette? minimapPalette;
    private bool minimapReverse;

    private float MinimapWidth => Minimap ? MINIMAP_WIDTH + MINIMAP_GAP : 0.0f;

    /// <summary>The rows the minimap shows, the scrollback and the grid, or the grid alone for a program with the whole screen.</summary>
    private (int Start, int Count) MinimapRows() => screen.AlternateScreen ? (screen.Scrollback.Count, screen.Rows) : (0, screen.TotalRows);

    /// <summary>How tall a row of it is, a whole number of the screen's pixels.</summary>
    private float MinimapRow() => UIMinimap.Row(UIMinimap.PixelsPerUnit(Module));

    /// <summary>How many whole rows the minimap is scrolled for where the view is, at its top with the view at the top and at its bottom with the view at the bottom.</summary>
    private int MinimapScrolled(float row, int top)
    {
        int most = screen.Scrollback.Count;
        return screen.AlternateScreen || most == 0 ? 0 : UIMinimap.ScrolledRows(MinimapRows().Count, row, minimapMap.Height, (float)top / most);
    }

    private void PaintMinimap(UIDrawList list, TerminalPalette palette)
    {
        MinimapRowsDrawn = 0;
        if (!Minimap || minimapArea.Width <= 0.0f || minimapArea.Height <= 0.0f)
            return;

        UIModule? module = Module;
        float pixels = UIMinimap.PixelsPerUnit(module);
        float row = UIMinimap.Row(pixels), character = UIMinimap.Whole(UIMinimap.CHARACTER, pixels), gap = UIMinimap.Gap(row, pixels);
        UIRect map = minimapMap = UIMinimap.OnPixels(module, minimapArea);

        // Every row is worked out again once the colours aren't the ones its bars were made with
        if (!ReferenceEquals(minimapPalette, palette) || minimapReverse != screen.ReverseVideo)
        {
            minimapPalette = palette;
            minimapReverse = screen.ReverseVideo;
            minimapGeneration++;
        }

        var (start, count) = MinimapRows();
        int top = RowOffset(), scrolledRows = MinimapScrolled(row, top);
        int first = Math.Min(scrolledRows, Math.Max(0, count - 1));
        int last = Math.Min(count - 1, first + (int)(map.Height / row));
        MinimapFirstRow = start + first;

        // Its rows are other rows from one tick to the next as it scrolls, nothing of it is blended between ticks
        list.BeginPart(PART_MINIMAP);
        list.BeginSteps();
        list.PushClip(map);
        list.Rect(map, palette.Foreground with { W = 0.025f });

        float left = UIMinimap.OnPixel(module, new Vector2(map.Min.X + 4.0f, 0.0f)).X;
        int most = Math.Min(MINIMAP_COLUMNS, (int)((map.Max.X - left) / character));
        for (int index = first; index <= last; index++)
        {
            TerminalRow shown = screen.RowAt(start + index);
            KeepMinimapRow(shown, start + index, palette);
            float rowTop = map.Max.Y - (index - scrolledRows) * row;
            if (shown.MinimapBarCount > 0)
                UIMinimap.Bars(list, shown.MinimapBars.AsSpan(0, shown.MinimapBarCount), left, character, rowTop, row, gap, most);

            // A command's prompt, a mark down the left edge in the colour of how it ended
            if (shown.Mark.Prompt)
            {
                Vector4 ink = shown.Mark.Exit is null ? palette.Foreground with { W = 0.4f } : shown.Mark.Exit == 0 ? palette.Ansi[2] : palette.Ansi[1];
                list.Rect(new UIRect(new Vector2(map.Min.X, rowTop - row), new Vector2(map.Min.X + 2.0f, rowTop)), ink);
            }
        }

        MinimapRowsDrawn = last - first + 1;

        // What a find found, down the right edge across the rows it's on
        for (int i = 0; i < matches.Count; i++)
        {
            int index = matches[i].Row - start;
            if (index < first || index > last)
                continue;

            float rowTop = map.Max.Y - (index - scrolledRows) * row;
            Vector4 lit = i == matchAt ? palette.Cursor with { W = 0.8f } : palette.Ansi[3] with { W = 0.6f };
            list.Rect(new UIRect(new Vector2(map.Max.X - 5.0f, rowTop - row), new Vector2(map.Max.X, rowTop)), lit);
        }

        // The slider over the rows in sight, only with a scrollback to go through
        if (!screen.AlternateScreen && screen.Scrollback.Count > 0)
        {
            Vector2 pointer = module is not null ? module.ToLocal(module.Compositor.Pointer.Position) : new Vector2(float.NaN);
            bool lit = draggingMinimap || (IsHovered && map.Contains(pointer));
            UIMinimap.Slider(list, module, map, map.Max.Y - (top - scrolledRows) * row, screen.Rows * row, palette.Foreground, lit);
        }

        list.PopClip();
        list.EndSteps();
    }

    /// <summary>
    /// Helper method to work out a row's bars when they aren't what it is now, a row of the scrollback once (it doesn't
    /// change there) and a row of the grid when the screen changed since. Nothing is allocated for a row that was done
    /// before unless it has more runs than it had room for.
    /// </summary>
    private void KeepMinimapRow(TerminalRow row, int index, TerminalPalette palette)
    {
        bool settled = index < screen.Scrollback.Count;
        long stamp = ((long)minimapGeneration << 32) | (settled ? 0xFFFFFFFFL : (uint)screen.Version);
        if (row.MinimapStamp == stamp)
            return;

        row.MinimapStamp = stamp;
        UIMinimapBar[]? bars = row.MinimapBars;
        int count = 0, columns = Math.Min(row.Length, MINIMAP_COLUMNS);
        int x = 0;
        while (x < columns)
        {
            if (!MinimapInk(row.Cells[x], palette, out Vector4 ink))
            {
                x++;
                continue;
            }

            // A run of cells in one colour is one bar, a blank cell ends it the way a space ends a word
            int end = x + 1;
            while (end < columns && MinimapInk(row.Cells[end], palette, out Vector4 next) && next == ink)
                end++;

            if (bars is null || count == bars.Length)
                Array.Resize(ref bars, Math.Max(8, count * 2));
            bars[count++] = new UIMinimapBar(x, end, ink);
            x = end;
        }

        row.MinimapBars = bars;
        row.MinimapBarCount = count;
    }

    /// <summary>The colour a cell is drawn in the minimap, its text's, or its background's when it's blank on one of its own. False for a blank cell.</summary>
    private bool MinimapInk(in TerminalCell cell, TerminalPalette palette, out Vector4 ink)
    {
        if ((cell.CodePoint > ' ' || cell.IsContinuation) && !cell.Style.Has(TerminalAttributes.Invisible))
        {
            ink = ForegroundOf(cell.Style, palette) with { W = MINIMAP_INK };
            return true;
        }

        Vector4 back = BackgroundOf(cell.Style, palette);
        if (back != palette.Background)
        {
            ink = back with { W = MINIMAP_BACK };
            return true;
        }

        ink = default;
        return false;
    }

    /// <summary>
    /// A press on the minimap, the slider taken hold of, or the row pressed brought to the middle and the slider held by
    /// its middle from there. A press on it with nothing to scroll is still its own, and does nothing. False off it.
    /// </summary>
    private bool PressMinimap(Vector2 point)
    {
        if (!Minimap || !minimapMap.Contains(point))
            return false;

        int most = screen.Scrollback.Count;
        if (screen.AlternateScreen || most == 0)
            return true;

        float row = MinimapRow();
        int top = RowOffset(), scrolledRows = MinimapScrolled(row, top);
        float down = (top - scrolledRows) * row, height = screen.Rows * row;
        float pointerDown = minimapMap.Max.Y - point.Y;

        if (pointerDown < down || pointerDown > down + height)
        {
            int at = scrolledRows + (int)(pointerDown / row);
            ScrollToRow(Math.Clamp(at - screen.Rows / 2, 0, most));
            minimapGrab = height * 0.5f;
        }
        else
        {
            minimapGrab = pointerDown - down;
        }

        draggingMinimap = true;
        RequestFrame();
        return true;
    }

    /// <summary>The slider dragged, the view scrolled to where it is. Where the slider sits goes along with the view in a straight line.</summary>
    private void DragMinimap(Vector2 point)
    {
        int most = screen.Scrollback.Count;
        if (screen.AlternateScreen || most == 0)
            return;

        float row = MinimapRow();
        float wanted = minimapMap.Max.Y - point.Y - minimapGrab;
        float atBottom = (most - MinimapScrolled(row, most)) * row;
        ScrollToRow(atBottom <= 0.0f ? 0 : (int)MathF.Round(Math.Clamp(wanted / atBottom, 0.0f, 1.0f) * most));
    }
}
