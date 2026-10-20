using System.Numerics;
using System.Text;

using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// What a terminal does because it knows the marks and the screen. The commands as blocks (TerminalScreen.Blocks), a
// mark in the gutter for each coloured by how it ended, jumping between them, a block or its output selected, the
// scrollback scrolled to a row, a find over all of it with every match lit, and the caret of the shell moved to a click
// with alt held. The keys and the commands that ask for these are the host's (the core plugin), this is what they do.
public partial class TerminalView
{
    // The gutter the marks are drawn in, on the left of the cells, there only once the shell has said a mark
    private const float GUTTER = 6.0f;

    /// <summary>The blocks of the scrollback, the oldest first, see <see cref="TerminalScreen.Blocks"/>.</summary>
    public List<TerminalBlock> Blocks() => screen.Blocks();

    /// <summary>The last block whose command has run, the one before the prompt being typed at, null for none.</summary>
    public TerminalBlock? LastRun()
    {
        var blocks = screen.Blocks();
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i].OutputRow >= 0 || i < blocks.Count - 1)
                return blocks[i];
        }

        return null;
    }

    /// <summary>The scrollback scrolled so a row of the whole is at the top, as far as there is scrollback for it.</summary>
    public void ScrollToRow(int row)
    {
        scrolled = Math.Clamp(screen.Scrollback.Count - row, 0, screen.Scrollback.Count);
        RequestFrame();
    }

    /// <summary>The view scrolled by rows, up for more than none, down for fewer.</summary>
    public void Scroll(int rows) => ScrollBy(rows);

    /// <summary>The top of the scrollback, or the bottom where the shell is.</summary>
    public void ScrollToEnd(bool top) => ScrollToRow(top ? 0 : screen.Scrollback.Count);

    /// <summary>The prompt before (-1) or after (1) the top of the view brought to the top, false when there is none that way.</summary>
    public bool JumpToCommand(int direction)
    {
        int top = RowOffset();
        var blocks = screen.Blocks();
        TerminalBlock? to = null;
        if (direction < 0)
        {
            for (int i = blocks.Count - 1; i >= 0 && to is null; i--)
                if (blocks[i].PromptRow < top) to = blocks[i];
        }
        else
        {
            foreach (TerminalBlock block in blocks)
            {
                if (block.PromptRow > top)
                {
                    to = block;
                    break;
                }
            }
        }

        if (to is not { } found)
        {
            if (direction > 0)
                ScrollToEnd(top: false);
            return false;
        }

        ScrollToRow(found.PromptRow);
        jumpedTo = found.PromptRow;
        return true;
    }

    // The prompt a jump went to, lit in the gutter for a moment
    private int jumpedTo = -1;

    /// <summary>Selects from one cell of the whole to another, both in, as a drag would.</summary>
    public void Select(int fromRow, int fromColumn, int toRow, int toColumn)
    {
        selectionAnchor = (Math.Max(0, fromColumn), Math.Max(0, fromRow));
        selectionEnd = (Math.Max(0, toColumn), Math.Max(0, toRow));
        hasSelection = true;
        RequestFrame();
    }

    /// <summary>The whole of a block selected, its prompt to its last row.</summary>
    public void SelectBlock(in TerminalBlock block) => Select(block.PromptRow, 0, Math.Max(block.PromptRow, block.EndRow), screen.Columns - 1);

    /// <summary>A block's output selected, false for one that wrote nothing.</summary>
    public bool SelectOutput(in TerminalBlock block)
    {
        if (block.OutputRow < 0 || block.OutputRow > block.EndRow)
            return false;

        Select(block.OutputRow, 0, block.EndRow, screen.Columns - 1);
        ScrollToRow(Math.Min(block.PromptRow, screen.Scrollback.Count));
        return true;
    }

    /* Find */

    private readonly List<(int Row, int Column, int Length)> matches = [];
    private int matchAt = -1;

    /// <summary>What was looked for last, empty for nothing.</summary>
    public string Finding { get; private set; } = string.Empty;

    /// <summary>How many places what was looked for is, and which of them is the one stepped to (0 based, -1 for none).</summary>
    public (int Count, int At) Found => (matches.Count, matchAt);

    /// <summary>Looks for a text in all of it, case aside, every match lit, the nearest above the bottom stepped to. How many there are.</summary>
    public int Find(string text)
    {
        Finding = text;
        matches.Clear();
        matchAt = -1;
        if (text.Length == 0)
        {
            RequestFrame();
            return 0;
        }

        for (int row = 0; row < screen.TotalRows; row++)
        {
            string line = screen.RowAt(row).Text();
            for (int at = line.IndexOf(text, StringComparison.OrdinalIgnoreCase); at >= 0; at = line.IndexOf(text, at + 1, StringComparison.OrdinalIgnoreCase))
                matches.Add((row, at, text.Length));
        }

        if (matches.Count > 0)
            StepTo(matches.Count - 1);
        RequestFrame();
        return matches.Count;
    }

    /// <summary>The match after (1) or before (-1) the one stepped to, round the ends, false when there are none.</summary>
    public bool FindNext(int direction)
    {
        if (matches.Count == 0)
            return false;

        StepTo(((matchAt < 0 ? 0 : matchAt) + direction + matches.Count) % matches.Count);
        return true;
    }

    /// <summary>The matches let go of.</summary>
    public void ClearFind()
    {
        Finding = string.Empty;
        matches.Clear();
        matchAt = -1;
        RequestFrame();
    }

    private void StepTo(int index)
    {
        matchAt = index;
        var (row, column, length) = matches[index];
        Select(row, column, row, column + length - 1);

        // In sight, a few rows under the top
        if (row < RowOffset() || row >= RowOffset() + screen.Rows)
            ScrollToRow(Math.Max(0, row - screen.Rows / 3));
    }

    /* Moving the shell's caret to a click */

    /// <summary>
    /// The shell's caret moved to a cell on the line it is on, by as many left or right arrows as it is away, which is
    /// what alt and a click do at a prompt. False for a click on another row or a program that has the whole screen.
    /// </summary>
    public bool MoveCaretTo(int column, int rowOfGrid)
    {
        if (screen.AlternateScreen || rowOfGrid != screen.CursorY || scrolled != 0 || (process is null && program is null))
            return false;

        int by = Math.Clamp(column, 0, screen.Columns - 1) - screen.CursorX;
        if (by == 0)
            return true;

        string arrow = by > 0 ? (screen.ApplicationCursorKeys ? "\u001bOC" : "\u001b[C") : (screen.ApplicationCursorKeys ? "\u001bOD" : "\u001b[D");
        var keys = new StringBuilder();
        for (int i = 0; i < Math.Abs(by); i++)
            keys.Append(arrow);
        Write(keys.ToString());
        return true;
    }

    /* Waiting for the shell */

    private string? sendWhenReady;
    private float readyWait;

    /// <summary>Text sent once the shell is at its first prompt (its marks say so, or it has written something and gone quiet), a line to run in a new split.</summary>
    public void SendWhenReady(string text)
    {
        sendWhenReady = text;
        readyWait = 0.0f;
    }

    private void UpdateSendWhenReady(float dt)
    {
        if (sendWhenReady is null)
            return;

        readyWait += dt;
        bool ready = screen.MarksSeen ? screen.AtPrompt : screen.Text().Trim().Length > 0 && readyWait > 0.6f;
        if (ready || readyWait > 8.0f)
        {
            Write(sendWhenReady);
            sendWhenReady = null;
        }
    }

    /* Painting */

    /// <summary>The gutter's marks and the matches of a find, over the rows painted. Nothing allocated.</summary>
    private void PaintMarks(UIDrawList list, int offset, int rows, float top, TerminalPalette palette)
    {
        if (screen.MarksSeen || jumpedTo >= 0)
        {
            float left = Bounds.Min.X + 1.0f;
            for (int y = 0; y < rows; y++)
            {
                int index = offset + y;
                if (index >= screen.TotalRows)
                    break;

                TerminalRowMark mark = screen.RowAt(index).Mark;
                if (!mark.Prompt)
                    continue;

                Vector4 ink = mark.Exit is null ? palette.Foreground with { W = 0.3f } : mark.Exit == 0 ? palette.Ansi[2] : palette.Ansi[1];
                if (index == jumpedTo)
                    ink = palette.Cursor;
                float rowTop = top - y * cellHeight;
                list.Rect(new UIRect(new Vector2(left, rowTop - cellHeight), new Vector2(left + 2.0f, rowTop)), ink);
            }
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var (row, column, length) = matches[i];
            if (row < offset || row >= offset + rows)
                continue;

            float rowTop = top - (row - offset) * cellHeight;
            Vector4 lit = i == matchAt ? palette.Cursor with { W = 0.45f } : palette.Ansi[3] with { W = 0.28f };
            list.Rect(new UIRect(new Vector2(paintedArea.Min.X + column * cellWidth, rowTop - cellHeight), new Vector2(paintedArea.Min.X + (column + length) * cellWidth, rowTop)), lit);
        }
    }

    /// <summary>The block whose prompt is on a row of the whole, null for a row that isn't one's prompt.</summary>
    private TerminalBlock? BlockAt(int row)
    {
        foreach (TerminalBlock block in screen.Blocks())
        {
            if (block.PromptRow <= row && row <= Math.Max(block.PromptRow, block.EndRow))
                return block;
        }

        return null;
    }

    /// <summary>A press in the gutter, the block there selected whole. False for a press anywhere else.</summary>
    private bool GutterPressed(Vector2 point, int row)
    {
        if (!screen.MarksSeen || point.X > paintedArea.Min.X)
            return false;

        if (BlockAt(row) is { } block)
        {
            SelectBlock(block);
            return true;
        }

        return false;
    }
}
