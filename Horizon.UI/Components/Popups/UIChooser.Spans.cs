namespace Horizon.UI.Components;

// Which letters of a row light up for what was typed, the same ways a row matches that Rank goes by, and what a file is
// for putting the leftovers (backups, pictures, what gets built) after the code
public static partial class UIChooser
{
    /// <summary>
    /// The pieces of a text that match what was typed, each where it starts and how long it is, in order. The same ways
    /// <see cref="Rank"/> goes by, best first, the text starting with it, a word of it starting with it, it in there whole,
    /// the letters each starting a word, then the letters in a few pieces. Nothing for nothing typed or no match.
    /// </summary>
    public static List<(int Start, int Length)> MatchSpans(string text, string typed)
    {
        string query = typed.Trim();
        if (query.Length == 0 || text.Length == 0)
            return [];

        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return [(0, query.Length)];

        int whole = -1;
        for (int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(query, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (WordStart(text, at))
                return [(at, query.Length)];
            if (whole < 0)
                whole = at;
        }

        if (whole >= 0)
            return [(whole, query.Length)];

        return InitialSpans(text, query) ?? RunSpans(text, query) ?? [];
    }

    // The letters typed each on the start of a word, the way Initials finds them, null when they aren't
    private static List<(int Start, int Length)>? InitialSpans(string text, string typed)
    {
        var at = new List<int>();
        int from = 0;
        foreach (char letter in typed)
        {
            if (char.IsWhiteSpace(letter))
                continue;

            while (from < text.Length && !(Same(text[from], letter) && WordStart(text, from)))
                from++;
            if (from == text.Length)
                return null;
            at.Add(from++);
        }

        return Joined(at);
    }

    // The letters in no more than MOST_RUNS pieces, the first anywhere and the rest each starting a word, FewRuns' table
    // kept whole this time so the way back through it says where every letter landed. Null when it takes more pieces
    private static List<(int Start, int Length)>? RunSpans(string text, string typed)
    {
        string letters = string.Concat(typed.Where(letter => !char.IsWhiteSpace(letter)));
        if (letters.Length == 0 || letters.Length > text.Length)
            return null;

        const int NONE = int.MaxValue / 2;
        var runs = new int[letters.Length, text.Length];
        for (int t = 0; t < text.Length; t++)
            runs[0, t] = Same(text[t], letters[0]) ? 1 : NONE;

        for (int i = 1; i < letters.Length; i++)
        {
            int fewest = NONE;
            for (int t = 0; t < text.Length; t++)
            {
                int best = NONE;
                if (Same(text[t], letters[i]))
                {
                    if (t > 0)
                        best = runs[i - 1, t - 1];
                    if (WordStart(text, t))
                        best = Math.Min(best, fewest + 1);
                }

                runs[i, t] = best;
                fewest = Math.Min(fewest, runs[i - 1, t]);
            }
        }

        // Where the last letter landed in the fewest pieces, then back a letter at a time, going on with the piece
        // it's in where that's as few, a new piece starting at a word where it isn't
        int last = letters.Length - 1, end = -1;
        for (int t = 0; t < text.Length; t++)
        {
            if (runs[last, t] < NONE && (end < 0 || runs[last, t] < runs[last, end]))
                end = t;
        }

        if (end < 0 || runs[last, end] > MOST_RUNS)
            return null;

        var at = new int[letters.Length];
        at[last] = end;
        for (int i = last; i > 0; i--)
        {
            int here = at[i], pieces = runs[i, here];
            if (here > 0 && runs[i - 1, here - 1] == pieces)
            {
                at[i - 1] = here - 1;
                continue;
            }

            int before = here - 1;
            while (before >= 0 && runs[i - 1, before] + 1 != pieces)
                before--;
            if (before < 0)
                return null;
            at[i - 1] = before;
        }

        return Joined(at);
    }

    // Letters next to each other made one piece, so a run of them is lit as one
    private static List<(int Start, int Length)> Joined(IReadOnlyList<int> at)
    {
        var spans = new List<(int Start, int Length)>();
        foreach (int index in at)
        {
            if (spans.Count > 0 && spans[^1].Start + spans[^1].Length == index)
                spans[^1] = (spans[^1].Start, spans[^1].Length + 1);
            else
                spans.Add((index, 1));
        }

        return spans;
    }

    /// <summary>
    /// How far down a file goes among others as good, 0 for code and anything else that reads, 1 for pictures and what
    /// gets built, 2 for backups and what a merge or an editor leaves lying about (.orig, .bak, .rej, .tmp, .old, a name
    /// ending in a tilde).
    /// </summary>
    public static int Leftover(string path)
    {
        if (path.EndsWith('~'))
            return 2;

        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".orig" or ".bak" or ".rej" or ".tmp" or ".old" or ".swp")
            return 2;

        return UIFileKinds.IsText(path) && UIFileKinds.IconOf(path) != "file-media" ? 0 : 1;
    }
}
