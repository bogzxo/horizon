namespace Horizon.UI.Components;

// The order a search leaves the rows in, best first. A row scores by where each word typed was found in it, its name
// over its key over the other words it's known by over its choices over its sentence, and a name that's all of what
// was typed over anything. The sections go by the best row in them and the rows inside a section by their own score,
// equal ones keeping the order they were made in. With nothing typed it's the order they were made in again
public partial class SettingsPage
{
    // Where a word was found in a row, best first. A word in its name's start, at the start of a word of its name, or
    // anywhere in it, then its key, its keywords, one of its choices, its sentence or the line of a choice, its section
    private const int TITLE_START = 70, TITLE_WORD = 60, IN_TITLE = 55, IN_KEY = 40, IN_KEYWORDS = 30, IN_CHOICE = 20, IN_HELP = 10, IN_SECTION = 5;

    // The same found only once the word and the name, key or keywords are run together (tabsize in Tab size), a little
    // under finding it as it was typed so a word as it's written still goes first
    private const int SQUASHED_TITLE = 50, SQUASHED_KEY = 35, SQUASHED_KEYWORDS = 25;

    // A name that's the whole of what was typed, for every word of it, so Font is first for font among the rest. A key
    // or a keyword that's the whole of it run together comes next, "word wrap" is wrap_code's wordwrap
    private const int WHOLE_TITLE = 100, WHOLE_KEY = 90;

    // What a row of the part the page is for has over the rest. Code font in Dawn is over the terminal's Font for font,
    // a word in a name here over a whole name there, and a word only in the keywords here isn't
    private const int HOME = 50;

    // A section as the page made it, its heading, its rows and its panels, for the search to put back in order
    private sealed class BuiltSection(string group, Label header)
    {
        public string Group { get; } = group;

        public Label Header { get; } = header;

        public List<SettingRow> Rows { get; } = [];

        public List<UIComponent> Panels { get; } = [];
    }

    // The sections in the order they were made in, and in the order a search put them in, null while that's the same
    private readonly List<BuiltSection> built = [];
    private List<BuiltSection>? ranked;

    // The sections and their headings down the page in the order they're showing
    private IEnumerable<(string Group, Label Header)> SectionsInOrder() => (ranked ?? built).Select(section => (section.Group, section.Header));

    /// <summary>The keys of the rows showing in the order they are down the page, for a test of what a search put first.</summary>
    public IReadOnlyList<string> KeysShown => [.. rows.Children.OfType<SettingRow>().Where(row => row.Visible).Select(row => row.Setting.Key)];

    /// <summary>
    /// How well a word typed in the search goes with a setting, 0 for not at all. Its name's start is best, then a word
    /// of its name, anywhere in its name, its key, its keywords, one of its choices, its sentence and its section last.
    /// </summary>
    public static int Score(UISetting setting, string word)
    {
        string title = setting.Title;
        if (title.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            return TITLE_START;

        int at = title.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (at > 0)
            return char.IsLetterOrDigit(title[at - 1]) ? IN_TITLE : TITLE_WORD;

        // Run together it still counts, tabsize in Tab size and tab_size
        string compact = Compact(word);
        bool squashed = compact.Length > 0;
        if (squashed && Compact(title).Contains(compact, StringComparison.Ordinal))
            return SQUASHED_TITLE;
        if (setting.Key.Contains(word, StringComparison.OrdinalIgnoreCase))
            return IN_KEY;
        if (squashed && Compact(setting.Key).Contains(compact, StringComparison.Ordinal))
            return SQUASHED_KEY;
        if (setting.Keywords.Contains(word, StringComparison.OrdinalIgnoreCase))
            return IN_KEYWORDS;
        if (squashed && Compact(setting.Keywords).Contains(compact, StringComparison.Ordinal))
            return SQUASHED_KEYWORDS;
        if (ChoiceLabelOf(setting, word) is not null)
            return IN_CHOICE;
        if (setting.Help.Contains(word, StringComparison.OrdinalIgnoreCase) || ChoiceMatches(setting, word))
            return IN_HELP;
        return setting.Group.Contains(word, StringComparison.OrdinalIgnoreCase) ? IN_SECTION : 0;
    }

    /// <summary>How well every word typed goes with a setting, the words' scores together and more for a name that's all of them.</summary>
    public static int Score(UISetting setting, IReadOnlyList<string> words)
    {
        int score = 0;
        foreach (string word in words)
            score += Score(setting, word);

        if (words.Count == 0 || Compact(string.Concat(words)) is not { Length: > 0 } whole)
            return score;

        if (Compact(setting.Title) == whole)
            score = Math.Max(score, WHOLE_TITLE * words.Count);
        else if (words.Count > 1 && (Compact(setting.Key) == whole || setting.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(keyword => Compact(keyword) == whole)))
            score = Math.Max(score, WHOLE_KEY * words.Count);
        return score;
    }

    // A row's score on this page, a row of the part it's for a little over the rest
    private int RowScore(UISetting setting, string[] words) =>
        Score(setting, words) + (HomePart is { } home && PartOf(setting.Group, texts.General) == home ? HOME : 0);

    // The choice a word is in, as it's written or as its dropdown says it, null for none
    private static string? ChoiceLabelOf(UISetting setting, string word)
    {
        string[] choices = setting.Choices ?? [];
        for (int at = 0; at < choices.Length; at++)
        {
            string label = setting.LabelAt(at, choices[at]);
            if (label.Contains(word, StringComparison.OrdinalIgnoreCase) || choices[at].Contains(word, StringComparison.OrdinalIgnoreCase))
                return label;
        }

        return null;
    }

    /// <summary>
    /// What a row was found by when no word typed is in its name, key or sentence, the keyword or the choice it was
    /// (wordwrap for Wrap code), each once. Null when every word is somewhere the row shows. The row says it after its key.
    /// </summary>
    public static string? FoundBy(UISetting setting, IReadOnlyList<string> words)
    {
        List<string>? found = null;
        foreach (string word in words)
        {
            string? by = Score(setting, word) switch
            {
                IN_KEYWORDS or SQUASHED_KEYWORDS => KeywordOf(setting, word),
                IN_CHOICE => ChoiceLabelOf(setting, word),
                _ => null
            };
            if (by is null)
                continue;

            found ??= [];
            if (!found.Contains(by, StringComparer.OrdinalIgnoreCase))
                found.Add(by);
        }

        return found is null ? null : string.Join(", ", found);
    }

    // The keyword a word is in, the one it runs together when it's none of them alone (wordwrap of word wrap), the word
    // itself when it spans a few
    private static string KeywordOf(UISetting setting, string word)
    {
        string compact = Compact(word);
        string[] keywords = setting.Keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string keyword in keywords)
        {
            if (keyword.Contains(word, StringComparison.OrdinalIgnoreCase) || compact.Length > 0 && Compact(keyword).Contains(compact, StringComparison.Ordinal))
                return keyword;
        }

        return word;
    }

    // The rows best first for the words typed, or back in the order they were made in for none. The first row showing
    // is the one Down in the search goes to, it's the first child of the rows that has a control. The commonly used go
    // in the order they were given, at the top, whatever else is typed
    private void Rank(string[] words, bool onlyCommon)
    {
        if (onlyCommon)
        {
            ranked = [.. built];
            var first = new List<SettingRow>();
            foreach (string key in common)
            {
                if (rowOf.TryGetValue(key, out SettingRow? row))
                    first.Add(row);
            }

            PutInOrder(built, section => section.Rows, first);
            return;
        }

        if (words.Length == 0)
        {
            if (ranked is null)
                return;
            ranked = null;
            PutInOrder(built, section => section.Rows);
            return;
        }

        var scores = new Dictionary<SettingRow, int>();
        foreach (SettingRow row in rowOf.Values)
        {
            if (row.Visible)
                scores[row] = RowScore(row.Setting, words) + words.Count(word => Score(row.Setting, word) == 0 && row.PairsMatch(word)) * IN_KEYWORDS;
        }

        // A section by its best row, one that's showing only for its panel last of those showing
        int Best(BuiltSection section) =>
            section.Rows.Where(row => row.Visible).Select(row => scores.GetValueOrDefault(row)).DefaultIfEmpty(section.Header.Visible ? 0 : -1).Max();

        ranked = [.. built.Select((section, at) => (section, at)).OrderByDescending(pair => Best(pair.section)).ThenBy(pair => pair.at).Select(pair => pair.section)];
        PutInOrder(ranked, section => [.. section.Rows.Select((row, at) => (row, at))
            .OrderByDescending(pair => pair.row.Visible ? scores.GetValueOrDefault(pair.row) : -1).ThenBy(pair => pair.at).Select(pair => pair.row)]);
    }

    // Every heading, row and panel moved to where it goes among the rows, only those not already there. Rows put first
    // go over everything, each once
    private void PutInOrder(IEnumerable<BuiltSection> sections, Func<BuiltSection, IEnumerable<SettingRow>> rowsOf, IReadOnlyList<SettingRow>? first = null)
    {
        int at = 0;
        void Place(UIComponent child)
        {
            IReadOnlyList<UIComponent> children = rows.Children;
            if (at >= children.Count || !ReferenceEquals(children[at], child))
                rows.MoveChild(child, at);
            at++;
        }

        var placed = new HashSet<SettingRow>();
        foreach (SettingRow row in first ?? [])
        {
            if (placed.Add(row))
                Place(row);
        }

        foreach (BuiltSection section in sections)
        {
            Place(section.Header);
            foreach (SettingRow row in rowsOf(section))
            {
                if (!placed.Contains(row))
                    Place(row);
            }
            foreach (UIComponent panel in section.Panels)
                Place(panel);
        }
    }
}
