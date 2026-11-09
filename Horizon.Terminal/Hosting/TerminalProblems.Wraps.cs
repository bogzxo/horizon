using System.Text.RegularExpressions;

namespace Horizon.UI.Components;

// Windows' console (ConPTY) draws a line too long for the terminal up to its edge, then a new line, then puts the
// cursor back on the last column of the row before and writes that last letter again, so the terminal wraps the rest
// itself. On the screen it's one line, in the stream it's two, and a build's warning came out as half a path on one and
// the rest on the next (zon.UI\ListBox.cs, or l.cs), with its message cut off at the edge. So a line that reached the
// edge is held back until the next one starts drawing, and joined to it when that starts by going back to the edge to
// write the same letter again. It also draws over what it drew (a build's progress, a prompt), a carriage return on its
// own or the cursor put at the start of a row, and what comes after that is a line of its own and not the end of the
// one before it.
public sealed partial class TerminalProblemReader
{
    // An escape sequence right where it's looked for, and the cursor put somewhere (CSI row;column H) with its column
    private static readonly Regex EscapeHere = new(@"\G\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled);
    private static readonly Regex Placed = new(@"^\x1B\[\d*;(?<column>\d+)[Hf]$", RegexOptions.Compiled);

    // The cursor put at the start of a row (CSI H, CSI row H, CSI row;1 H)
    private static readonly Regex RowStart = new(@"^\x1B\[\d*(?:;1)?[Hf]$", RegexOptions.Compiled);

    // A line that ran to the terminal's edge, waiting to see whether the next carries on with it
    private string? broken;

    // Where the last row of the line being read started (the edge, for the rest of a line broken there), and how much
    // of it was drawn on the rows before
    private int startsAt = 1, drawnBefore;

    /// <summary>A new line in the stream, the line read so far held back when it reached the edge, read otherwise.</summary>
    private void Ended()
    {
        string raw = pending.ToString();
        pending.Clear();

        // Nothing came between, the line held back was a line of its own
        if (broken is not null)
        {
            Line(broken);
            broken = null;
        }

        int width = columns?.Invoke() ?? 0, drawn = Drawn(raw, raw.Length) - drawnBefore;

        // The letter written again on the edge and then a new line straight after, the rest drawn from the start of the
        // row under it (ConPTY does that when the rest starts with a space). Still the one line, the new line goes
        if (startsAt == width && width > 0 && drawn == 1)
        {
            pending.Append(raw);
            drawnBefore += drawn;
            startsAt = 1;
            return;
        }

        bool atEdge = width > 0 && drawn > 0 && (startsAt - 1 + drawn - 1) % width + 1 == width;
        startsAt = 1;
        drawnBefore = 0;
        if (atEdge)
            broken = raw;
        else
            Line(raw);
    }

    /// <summary>
    /// Something came after a line that was held back. Escape sequences are waited out, the first letter drawn says
    /// whether it carries on with it, the cursor put back on the edge and the letter the line ended with written again.
    /// </summary>
    private void Continued()
    {
        string so = pending.ToString();
        int at = 0;
        string? last = null;
        while (at < so.Length)
        {
            if (so[at] != '\u001b')
                break;

            Match escape = EscapeHere.Match(so, at);
            if (!escape.Success)
                return; // the rest of it is still on its way

            last = escape.Value;
            at += escape.Length;
        }

        if (at >= so.Length)
            return;

        string line = broken!;
        broken = null;
        int width = columns?.Invoke() ?? 0, letter = LastDrawn(line);
        bool carriesOn = last is not null && Placed.Match(last) is { Success: true } placed && int.TryParse(placed.Groups["column"].Value, out int column)
            && column == width && letter >= 0 && line[letter] == so[at];
        if (!carriesOn)
        {
            Line(line);
            return;
        }

        // The letter written again is this row's first, the rows before it the line so far
        string before = line.Remove(letter, 1);
        pending.Insert(0, before);
        drawnBefore = Drawn(before, before.Length);
        startsAt = width;
    }

    // How many letters some of a line draws, the escape sequences and the other control characters in it left out
    private static int Drawn(string raw, int upTo)
    {
        int count = 0;
        for (int at = 0; at < upTo;)
        {
            if (raw[at] == '\u001b' && EscapeHere.Match(raw, at) is { Success: true } escape)
            {
                at += escape.Length;
                continue;
            }

            if (!char.IsControl(raw[at]))
                count++;
            at++;
        }

        return count;
    }

    /// <summary>
    /// The cursor went back to the start of the line on its own, and what comes next is drawn over it. What was drawn
    /// goes, its escape sequences stay (a prompt's marks among them).
    /// </summary>
    private void DrawnOver()
    {
        string so = pending.ToString();
        pending.Clear();
        for (int at = 0; at < so.Length;)
        {
            if (so[at] == '\u001b' && EscapeHere.Match(so, at) is { Success: true } escape)
            {
                pending.Append(escape.Value);
                at += escape.Length;
                continue;
            }

            at++;
        }

        startsAt = 1;
        drawnBefore = 0;
    }

    /// <summary>
    /// A sequence that moves the cursor may have just ended the line read so far. One that puts it at the start of a row
    /// ends the line there, what was read of it is read and what comes is a line of its own. A progress line drawn over
    /// by a warning had the warning read as the end of the progress, and nothing matched it.
    /// </summary>
    private void Jumped()
    {
        int escape = pending.Length - 2;
        while (escape >= 0 && pending.Length - escape <= 16 && pending[escape] != '\u001b')
            escape--;
        if (escape < 0 || pending[escape] != '\u001b' || !RowStart.IsMatch(pending.ToString(escape, pending.Length - escape)))
            return;

        string before = pending.ToString(0, escape);
        pending.Clear();
        startsAt = 1;
        drawnBefore = 0;
        Line(before);
    }

    // Where the last letter a line draws is in it, -1 for a line that draws none
    private static int LastDrawn(string raw)
    {
        int last = -1;
        for (int at = 0; at < raw.Length;)
        {
            if (raw[at] == '\u001b' && EscapeHere.Match(raw, at) is { Success: true } escape)
            {
                at += escape.Length;
                continue;
            }

            if (!char.IsControl(raw[at]))
                last = at;
            at++;
        }

        return last;
    }
}
