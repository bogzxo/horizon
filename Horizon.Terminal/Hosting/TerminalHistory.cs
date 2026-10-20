using System.Text;

namespace Horizon.UI.Components;

/// <summary>
/// The lines that were run, in every shell of every tab, kept in one file (history.txt next to the settings) and
/// stepped through with the arrows (TerminalHost). The newest last, a line the same as the one before it kept
/// once, blank lines not at all, at most <see cref="Limit"/> of them. The file is read as the program starts and written
/// after every line, so a second program sees what the first ran once it starts.
/// </summary>
public sealed class TerminalHistory
{
    public const string FILE = "history.txt";
    public const int DEFAULT_LIMIT = 1000;

    private readonly List<string> lines = [];
    private readonly string? path;

    /// <summary>The file it is kept in, null for one that lives as long as the run.</summary>
    public string? Path => path;

    /// <summary>How many lines are kept at most, the oldest let go of first.</summary>
    public int Limit { get; set; } = DEFAULT_LIMIT;

    public IReadOnlyList<string> Lines => lines;

    public int Count => lines.Count;

    /// <param name="path">The file it is kept in, null for a history that lives as long as the run (the self test).</param>
    public TerminalHistory(string? path)
    {
        this.path = path;
        if (path is null || !File.Exists(path))
            return;

        try
        {
            foreach (string line in File.ReadAllLines(path))
                Add(line, save: false);
        }
        catch (Exception)
        {
            // A history that doesn't read is no history, nothing to say about it
        }
    }

    /// <summary>Puts a line in, last, unless it is blank or the same as the last one. True if it went in.</summary>
    public bool Add(string line, bool save = true)
    {
        line = line.Trim();
        if (line.Length == 0 || (lines.Count > 0 && lines[^1] == line))
            return false;

        lines.Add(line);
        while (lines.Count > Limit)
            lines.RemoveAt(0);

        if (save)
            Save();
        return true;
    }

    public void Clear()
    {
        lines.Clear();
        Save();
    }

    private void Save()
    {
        if (path is null)
            return;

        try
        {
            var text = new StringBuilder();
            foreach (string line in lines)
                text.Append(line).Append('\n');
            File.WriteAllText(path, text.ToString());
        }
        catch (Exception)
        {
            // Kept for the run at least
        }
    }

    /// <summary>
    /// Where a shell is in the history as the arrows step it. <see cref="At"/> is the line on the prompt, <see cref="Count"/>
    /// of the history when it is the line that was being typed before the stepping started (<see cref="Saved"/>).
    /// </summary>
    public sealed class Cursor
    {
        public int At { get; set; } = -1;
        public string Saved { get; set; } = string.Empty;

        /// <summary>Back to typing, the next arrow starts from the newest line again.</summary>
        public void Reset()
        {
            At = -1;
            Saved = string.Empty;
        }
    }

    /// <summary>
    /// The line an arrow brings up, up (-1) for the one before, down (1) for the one after, from a line that is being
    /// typed, which is kept and comes back past the newest. Null when there is nothing in that direction.
    /// </summary>
    public string? Step(Cursor cursor, int direction, string typing)
    {
        if (lines.Count == 0 || direction == 0)
            return null;

        if (cursor.At < 0)
        {
            cursor.At = lines.Count;
            cursor.Saved = typing;
        }

        int next = cursor.At + Math.Sign(direction);
        if (next < 0 || next > lines.Count)
            return null;

        cursor.At = next;
        return next == lines.Count ? cursor.Saved : lines[next];
    }
}
