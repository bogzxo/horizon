using System.Numerics;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>
/// A gap kept open in the code for something a program puts there, Zed's inline assistant box: a block between two lines,
/// so the code under it moves down and nothing is covered. It's a run of rows with none of the code on them (the way a
/// lens row is), so every place that goes by rows (painting, hits, the caret, scrolling, the gutter) steps over it.
/// </summary>
/// <param name="Owner">Who keeps it, one gap an owner.</param>
/// <param name="Line">The line it hangs on, counted from 0. It moves with the line as lines go in or out above it.</param>
/// <param name="Above">Over the line when true (it may be line 0), under the line and the lines a prediction adds under it when false.</param>
/// <param name="Height">How tall it's asked to be in pixels. It's whole rows, so it takes the rows that hold this much.</param>
public readonly record struct UIBlockSpace(string Owner, int Line, bool Above, float Height);

/// <summary>The gap maths that needs no editor, kept apart so it can be checked on its own.</summary>
public static class BlockSpaceMath
{
    /// <summary>How many rows a gap of a height takes, at least one.</summary>
    public static int RowsFor(float height, float lineHeight) =>
        lineHeight <= 0.0f ? 1 : Math.Max(1, (int)MathF.Ceiling(height / lineHeight - 0.001f));

    /// <summary>
    /// Where to ease the scroll back to when the last gap closes: the place it was before the gap was scrolled into sight,
    /// when it's still where that scroll took it (<paramref name="current"/>, the goal or the scroll itself). Null when the
    /// person scrolled somewhere else meanwhile, which stays. Left alone, a goal made for the gap's rows stayed once they
    /// were gone, and the top of the file was left scrolled under the sticky lines.
    /// </summary>
    public static float? ScrollAfterGap(float current, float revealedTo, float origin) =>
        MathF.Abs(current - revealedTo) <= 1.0f && MathF.Abs(current - origin) > 0.5f ? origin : null;

    /// <summary>
    /// Where the line a gap hangs on is after lines were put in (by &gt; 0) or taken out (by &lt; 0) at a line, -1 when
    /// there's none left. A line pushed down by an enter before its text takes its gap down with it; lines taken out
    /// join the line they start on and the gap goes there.
    /// </summary>
    public static int MoveLine(int line, int at, int by, bool blankAtLine, int lineCount)
    {
        if (by > 0 && (line > at || (line == at && blankAtLine)))
            line += by;
        else if (by < 0 && line > at)
            line += by;
        else if (by < 0 && line > at + by)
            line = at + by;

        if (lineCount <= 0)
            return -1;
        return Math.Clamp(line, 0, lineCount - 1);
    }
}

public partial class CodeEditor
{
    private readonly List<UIBlockSpace> blockSpaces = [];

    // Where each gap's rows ended up as the rows were last made, by owner: the first row and how many
    private readonly Dictionary<string, (int First, int Count)> blockRows = [];

    // A count of the changes to the gaps, what the rows are made again for
    private int blocksChanged;

    private string? revealBlockOwner;

    // Where the scroll was before a gap was scrolled into sight and where that took it, for putting it back when the gap goes
    private (float Origin, float RevealedTo)? blockReveal;

    // The rows made at once for a change to the gaps, rather than by whatever paints next: the scroll that keeps the top line
    // in place moves with them, and the gutter and the code painted either side of it didn't agree for a frame
    private void MakeBlockRows()
    {
        if (paintedSkin is { } skin && document.LineCount > 0)
            EnsureRows(skin, paintedScale);
    }

    /// <summary>How many of the rows are the room of a gap (CodeEditor.SetBlockSpace) rather than any of the code.</summary>
    public int BlockRowCount { get; private set; }

    /// <summary>The last line a shown prediction adds rows under, -1 when none does. A gap under a diff goes under this line.</summary>
    public int PredictionLastLine => predictedRows.Length > 0 && PredictionShown ? predictedRows[^1].Line : -1;

    /// <summary>The gaps kept open now.</summary>
    public IReadOnlyList<UIBlockSpace> BlockSpaces => blockSpaces;

    /// <summary>
    /// Keeps a gap of a height open over a line or under it for an owner, the code under it moving down. Said again with the
    /// same things it does nothing, with other ones the gap moves or grows, one gap an owner.
    /// </summary>
    public void SetBlockSpace(string owner, int line, bool above, float height)
    {
        var wanted = new UIBlockSpace(owner, Math.Max(0, line), above, MathF.Max(0.0f, height));
        for (int i = 0; i < blockSpaces.Count; i++)
        {
            if (blockSpaces[i].Owner != owner)
                continue;
            if (blockSpaces[i] == wanted)
                return;
            blockSpaces[i] = wanted;
            blocksChanged++;
            MakeBlockRows();
            return;
        }

        blockSpaces.Add(wanted);
        blocksChanged++;
        MakeBlockRows();
    }

    /// <summary>Closes an owner's gap, the code under it moving back up.</summary>
    public void ClearBlockSpace(string owner)
    {
        if (blockSpaces.RemoveAll(block => block.Owner == owner) > 0)
        {
            blocksChanged++;
            if (revealBlockOwner == owner)
                revealBlockOwner = null;

            // The last gap gone: the scroll it took the view to goes with it
            if (blockSpaces.Count == 0 && blockReveal is { } reveal)
            {
                blockReveal = null;
                if (BlockSpaceMath.ScrollAfterGap(scrollGoal ?? scrollY, reveal.RevealedTo, reveal.Origin) is { } back)
                    scrollGoal = back;
            }
            MakeBlockRows();
        }
    }

    /// <summary>
    /// Where an owner's gap is as the code was last laid out, in the same coordinates as <see cref="CodeBounds"/>, across the
    /// whole code area. False when it has no rows yet or its line isn't shown (folded away). It follows the scroll, and may be
    /// out of sight above or below the area when scrolled away.
    /// </summary>
    public bool BlockSpaceBounds(string owner, out UIRect bounds)
    {
        bounds = default;
        if (paintedSkin is not { } skin || document.LineCount == 0)
            return false;

        float scale = paintedScale;
        float lineHeight = LineHeightOf(skin, scale);
        UIRect area = TextArea(skin, scale);
        EnsureRows(skin, scale);
        if (!blockRows.TryGetValue(owner, out var place) || place.Count <= 0)
            return false;

        float top = RowTop(area, place.First, lineHeight);
        bounds = new UIRect(new Vector2(area.Min.X, top - place.Count * lineHeight), new Vector2(area.Max.X, top));
        return true;
    }

    /// <summary>Scrolls just far enough for an owner's gap to be in sight, on the next update.</summary>
    public void RevealBlockSpace(string owner)
    {
        revealBlockOwner = owner;
        revealCaret = true;
    }

    // The rows of the gaps on a line (above it, or under it and under the lines a fold hides when it's folded, up to last)
    private void AddBlockRows(int line, int last, bool above, float lineHeight)
    {
        int length = document.LineAt(line).Length;
        for (int i = 0; i < blockSpaces.Count; i++)
        {
            UIBlockSpace block = blockSpaces[i];
            if (block.Above != above || (above ? block.Line != line : block.Line < line || block.Line > last))
                continue;

            int count = BlockSpaceMath.RowsFor(block.Height, lineHeight);
            blockRows[block.Owner] = (rows.Count, count);
            int at = above ? 0 : length;
            for (int row = 0; row < count; row++)
                rows.Add(new CodeRow(line, at, at, 0.0f, Block: true));
            BlockRowCount += count;
        }
    }

    // Lines put in or taken out: the gaps' lines go along with theirs
    private void MoveBlockSpaces(UITextPosition at, int by)
    {
        if (blockSpaces.Count == 0 || by == 0)
            return;

        bool blank = by > 0 && at.Line < document.LineCount && string.IsNullOrWhiteSpace(document.LineAt(at.Line));
        for (int i = 0; i < blockSpaces.Count; i++)
        {
            UIBlockSpace block = blockSpaces[i];
            int line = BlockSpaceMath.MoveLine(block.Line, at.Line, by, blank, document.LineCount);
            if (line >= 0 && line != block.Line)
                blockSpaces[i] = block with { Line = line };
        }

        blocksChanged++;
    }

    // Scrolls a gap into sight (Reveal's other half), the room the caret's line has over it counts for the caret already
    private void RevealBlock(UISkin skin, float scale, UIRect area, float lineHeight)
    {
        string? owner = revealBlockOwner;
        revealBlockOwner = null;
        if (owner is null || !blockRows.TryGetValue(owner, out var place) || place.Count <= 0)
            return;

        float top = place.First * lineHeight, bottom = (place.First + place.Count) * lineHeight;
        float from = scrollGoal ?? scrollY;
        float? wanted = top < from ? top : bottom > from + area.Height ? Math.Min(top, bottom - area.Height) : null;
        if (wanted is { } goal)
        {
            scrollGoal = MathF.Max(0.0f, goal);
            blockReveal = (blockReveal?.Origin ?? from, scrollGoal.Value);
        }
    }
}
