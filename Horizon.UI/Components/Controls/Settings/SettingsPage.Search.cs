using System.Numerics;

using Horizon.UI.Drawing;

using Silk.NET.Input;

namespace Horizon.UI.Components;

// The search over a settings page, what it leaves, how many that is, the x that empties it and the keys around it
public partial class SettingsPage
{
    /// <summary>The word in a search that leaves only the settings that aren't what they come as, VS Code's.</summary>
    public const string MODIFIED = "@modified";

    /// <summary>The word in a search that leaves only the settings people change the most (<see cref="Common"/>), in their order.</summary>
    public const string COMMON = "@common";

    /// <summary>What a word in a search starts with to leave only the setting of the key after it (<c>@id:wrap_code</c>), VS Code's.</summary>
    public const string ID = "@id:";

    /// <summary>
    /// What a word in a search starts with to leave only the sections whose name has the word after it
    /// (<c>@section:git</c>), the part before the dot or the section after it.
    /// </summary>
    public const string SECTION = "@section:";

    // How much of the right end of the box the count of what was found takes
    private const float FOUND_WIDTH = 150.0f;

    // What a lit word is wrapped in
    private const string ACCENT_ON = "[color:accent]", ACCENT_OFF = "[/color]";

    // How many the search found, the funnel of what it can be narrowed by, and the x that empties it, over the right end
    // of the box
    private readonly Label found;
    private readonly Button filters;
    private readonly Button clear;

    // Under what says a search found nothing, the way back out of it
    private readonly Button nothingClear;

    // Whether something is typed in the search, the sections down the left only show what it found while it is
    private bool searching;

    /// <summary>What the line at the end of the search says, "3 settings found", empty while nothing is typed. For a test.</summary>
    public string FoundText { get; private set; } = string.Empty;

    /// <summary>Whether the page says a search found nothing, with the button that empties it under that. For a test.</summary>
    public bool SaysNothingFound => nothing.Visible;

    /// <summary>What the page says when a search finds nothing, for what was typed last, without its colour. For a test.</summary>
    public string NothingText { get; private set; } = string.Empty;

    // What was typed the last time the rows were put in order, null while nothing is, a search typed afresh goes to the top
    private string? rankedFor;

    // Where the rows and the sections down the left were scrolled as a search started, and where they're to go back to
    // once it's emptied. The rows are laid out again first, so it's tried for a few updates till the offset holds
    private const int SCROLL_BACK_TRIES = 6;
    private (float Rows, float Nav)? scrolledBefore, scrollBack;
    private int scrollBackTries;

    /// <summary>Whether the page is still putting the scroll back to where it was before a search, for a test to wait on.</summary>
    public bool ScrollingBack => scrollBack is not null;

    /// <summary>How far the rows are scrolled, for a test.</summary>
    public float RowsOffset => scroll.Offset;

    // The rows and the sections put back where they were before the search, once they've been laid out again
    private void ScrollBack()
    {
        if (scrollBack is not { } back)
            return;

        scroll.Offset = back.Rows;
        navScroll.Offset = back.Nav;
        if ((MathF.Abs(scroll.Offset - back.Rows) < 0.5f && MathF.Abs(navScroll.Offset - back.Nav) < 0.5f) || --scrollBackTries <= 0)
            scrollBack = null;
    }

    // The rows whose name, key, sentence or one of its choices has every word typed in it, the sections with none of
    // them hidden. @modified among the words leaves only the changed ones as well, @id:key only the one by that key and
    // @section:word only the sections with the word in their name. @common leaves the ones people change the most, in
    // their own order and under no headings. Words run together count as well, "tab size" finds what "tabsize" does
    private void Filter(string typed)
    {
        var (words, onlyModified, onlyCommon, ids, sections) = Parse(typed);
        bool InSection(string group) => sections.Count == 0 || sections.Any(word => group.Contains(word, StringComparison.OrdinalIgnoreCase));
        string squashed = words.Length > 1 ? Compact(string.Concat(words)) : string.Empty;
        commonShown = onlyCommon;

        bool was = searching;
        searching = typed.Trim().Length > 0;

        // Where the rows and the sections were scrolled before a search, put back once it's emptied
        if (searching && !was)
            scrolledBefore = (scroll.Offset, navScroll.Offset);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int shown = 0;
        foreach (SettingRow row in rowOf.Values)
        {
            UISetting setting = row.Setting;
            row.Visible = (!onlyModified || IsModified?.Invoke(setting.Key) == true)
                && (!onlyCommon || common.Contains(setting.Key))
                && (ids.Count == 0 || ids.Contains(setting.Key, StringComparer.OrdinalIgnoreCase))
                && InSection(setting.Group)
                && (words.All(word => Matches(setting, word) || row.PairsMatch(word)) || squashed.Length > 0 && SquashedMatches(setting, squashed));
            if (row.Visible)
            {
                seen.Add(setting.Group);
                shown++;

                // What was typed lit in its name and its sentence the way VS Code does, so the eye lands on why it's here,
                // and the keyword or choice it was found by after its key when that's nowhere it shows
                row.Highlight(words);
                row.ShowFoundBy((FoundBy(setting, words) ?? PairFoundBy(row, words)) is { } by ? Highlighted(by, words) : null);
            }
        }

        // A panel by the name of its section or by what's written in it, it has no rows to search. Only its section's name
        // found it, and "add a language" found nothing though the panel said so in its heading. A panel is never changed
        // and has no key, so @modified and @id hide it, and @section does unless its section is one asked for
        foreach (var (group, content) in panels)
        {
            content.Visible = !onlyModified && !onlyCommon && ids.Count == 0 && InSection(group) && words.All(word => group.Contains(word, StringComparison.OrdinalIgnoreCase) || Says(content, word));
            if (content.Visible)
                seen.Add(group);
        }

        // The commonly used are one list from all over, no section's heading is over them
        foreach (var (name, header) in headerOf)
            header.Visible = !onlyCommon && seen.Contains(name);

        // Best first while words are typed, and a search typed afresh starts at the top, where the best of it is
        Rank(words, onlyCommon);
        if (searching && typed != rankedFor)
            scroll.Offset = 0.0f;
        rankedFor = searching ? typed : null;

        // Emptied, the page goes back to where it was before the search, the tree down the left as it was and the rows
        // scrolled as they were. Not when a row has the keyboard, one gone to from the search stays in sight
        if (was && !searching && scrolledBefore is { } before)
        {
            scrolledBefore = null;
            if (FocusedRow() is null)
                (scrollBack, scrollBackTries) = (before, SCROLL_BACK_TRIES);
        }

        // A search that leaves nothing says so rather than leaving the page blank, with a way back out under it
        nothing.Visible = seen.Count == 0 && searching;
        NothingText = SettingsTexts.Fill(texts.NothingFound, ("query", UIRichText.Plain(typed.Trim())));
        nothing.Text = $"[color:dim]{UIRichText.Literal(NothingText)}[/color]";
        nothing.TextScale = sentenceSize;
        nothingClear.Visible = nothing.Visible;
        nothingClear.LabelScale = sentenceSize;

        // How many it found at the end of the box, and the x after it, only while there's something typed
        FoundText = searching ? texts.Found(shown) : string.Empty;
        found.Text = $"[color:dim]{FoundText}[/color]";
        found.TextScale = sentenceSize * 0.9f;
        found.Visible = searching;
        clear.Visible = typed.Length > 0;

        // What's typed stops short of them rather than running under them, the funnel's always there
        float button = SearchHeight - 4.0f;
        float right = 10.0f + button + (clear.Visible ? button : 0.0f) + (found.Visible ? FOUND_WIDTH : 0.0f);
        search.Padding = new UIEdges(10.0f, 6.0f, right, 6.0f);
        navStale = true;
    }

    // What's typed split into its words, whether @modified and @common are among them, the keys of any @id: in it and
    // the words of any @section:, those taken out of the words. A word that's one of VS Code's ids is the name after its
    // dot, editor.tabSize is tabSize
    private static (string[] Words, bool OnlyModified, bool OnlyCommon, List<string> Ids, List<string> Sections) Parse(string typed)
    {
        var words = new List<string>();
        var ids = new List<string>();
        var sections = new List<string>();
        bool onlyModified = false, onlyCommon = false;
        foreach (string word in typed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals(MODIFIED, StringComparison.OrdinalIgnoreCase))
                onlyModified = true;
            else if (word.Equals(COMMON, StringComparison.OrdinalIgnoreCase))
                onlyCommon = true;
            else if (word.StartsWith(ID, StringComparison.OrdinalIgnoreCase))
            {
                // @id: with nothing after it yet (picked off the funnel, the key still to come) narrows nothing
                if (word.Length > ID.Length)
                    ids.Add(word[ID.Length..]);
            }
            else if (word.StartsWith(SECTION, StringComparison.OrdinalIgnoreCase))
            {
                // The same for @section: with its word still to come
                if (word.Length > SECTION.Length)
                    sections.Add(word[SECTION.Length..]);
            }
            else
                words.Add(Undotted(word));
        }

        return ([.. words], onlyModified, onlyCommon, ids, sections);
    }

    /// <summary>
    /// A word that's one of VS Code's ids (editor.tabSize, files.associations) as the name after its last dot, the part
    /// the rows here go by. Anything else as it is, *.orig and 1.5 among them, the dot has to have letters either side.
    /// </summary>
    public static string Undotted(string word)
    {
        int dot = word.LastIndexOf('.');
        return dot > 0 && dot < word.Length - 1 && char.IsLetter(word[dot - 1]) && char.IsLetter(word[dot + 1]) ? word[(dot + 1)..] : word;
    }

    // Whether all of what was typed run together is in a setting's name, key or keywords run together, "tab size" in
    // tab_size and "word wrap" in wordwrap
    private static bool SquashedMatches(UISetting setting, string squashed) =>
        Compact(setting.Title).Contains(squashed, StringComparison.Ordinal)
        || Compact(setting.Key).Contains(squashed, StringComparison.Ordinal)
        || Compact(setting.Keywords).Contains(squashed, StringComparison.Ordinal);

    // Whether a word is in a setting's name, key, sentence, section or one of its choices, as it's written or as the
    // dropdown says it ("subtle" finds the effects though neither their name nor their sentence says so), or in the line
    // that says what one of them does ("fast-forward" finds how to pull, only its label and its line say that). Its
    // keywords count as well, the words developers type that the page never shows (font size for the code's zoom), and
    // a word run together finds a name or key with spaces or underscores in it, wordwrap the keyword word wrap, tabsize
    // the Tab size and autosave auto_save
    internal static bool Matches(UISetting setting, string word)
    {
        if (setting.Title.Contains(word, StringComparison.OrdinalIgnoreCase)
            || setting.Key.Contains(word, StringComparison.OrdinalIgnoreCase)
            || setting.Help.Contains(word, StringComparison.OrdinalIgnoreCase)
            || setting.Group.Contains(word, StringComparison.OrdinalIgnoreCase)
            || setting.Keywords.Contains(word, StringComparison.OrdinalIgnoreCase)
            || ChoiceMatches(setting, word))
            return true;

        // Only a word with a letter or a digit in it, a lone dash would be nothing and found everything
        string compact = Compact(word);
        return compact.Length > 0 && SquashedMatches(setting, compact);
    }

    // The pair a row of pairs was found by when a word is in none of what it says but in one of its keys or values,
    // "*.orig = csharp" for orig, null otherwise
    private static string? PairFoundBy(SettingRow row, string[] words)
    {
        foreach (string word in words)
        {
            if (!Matches(row.Setting, word) && row.PairFoundBy(word) is { } pair)
                return pair;
        }

        return null;
    }

    /// <summary>A text in lower case with everything but its letters and digits taken out, "Tab size" and tab_size both tabsize.</summary>
    public static string Compact(string text)
    {
        if (text.Length == 0)
            return text;

        Span<char> kept = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        int count = 0;
        foreach (char each in text)
        {
            if (char.IsLetterOrDigit(each))
                kept[count++] = char.ToLowerInvariant(each);
        }

        return new string(kept[..count]);
    }

    private static bool ChoiceMatches(UISetting setting, string word)
    {
        string[] choices = setting.Choices ?? [];
        for (int at = 0; at < choices.Length; at++)
        {
            if (choices[at].Contains(word, StringComparison.OrdinalIgnoreCase)
                || setting.LabelAt(at, choices[at]).Contains(word, StringComparison.OrdinalIgnoreCase)
                || setting.HelpAt(at).Contains(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A text with every place one of the words is in it wrapped in the accent, whatever the case, for a row's name and
    /// sentence as a search lights them. Anything in the text a label would take for markup is made plain first. No
    /// words, or none of them in it, gives the text back as it is.
    /// </summary>
    public static string Highlighted(string text, IReadOnlyList<string> words)
    {
        string literal = UIRichText.Literal(text);
        if (words.Count == 0 || literal.Length == 0)
            return literal;

        // Which characters a word covers, so two words that overlap ("wra" and "rap") make one run rather than tags in tags
        var lit = new bool[literal.Length];
        bool any = false;
        foreach (string word in words)
        {
            if (word.Length == 0)
                continue;

            for (int at = literal.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = literal.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase))
            {
                Array.Fill(lit, true, at, word.Length);
                any = true;
            }
        }

        if (!any)
            return literal;

        var built = new System.Text.StringBuilder(literal.Length + 32);
        for (int i = 0; i < literal.Length; i++)
        {
            if (lit[i] && (i == 0 || !lit[i - 1]))
                built.Append(ACCENT_ON);
            built.Append(literal[i]);
            if (lit[i] && (i == literal.Length - 1 || !lit[i + 1]))
                built.Append(ACCENT_OFF);
        }

        return built.ToString();
    }

    // Whether a panel has a word in anything written in it, a label's text or a button's. Only what's showing, a word in
    // a part of it that's hidden (another profile's form, a folded section) found a panel that didn't show it anywhere.
    // Deeper in only short things count, headings, names and buttons, the labels that don't wrap. A sentence the panel
    // opens with still counts (the shortcuts' keybindings), but every sentence of a panel counting had wrap find
    // Extensions, Available through one extension's description down in its list. The panel itself is asked whatever
    // it is, the search hid it the last time and is asking again
    private static bool Says(UIComponent content, string word, int depth = 0)
    {
        string? written = content switch
        {
            Label { Wrap: false } label => label.Text,
            Label label when depth <= 1 => label.Text,
            Button button => button.Label,
            _ => null
        };
        if (written?.Contains(word, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        foreach (UIComponent child in content.Children)
        {
            if (child.Visible && Says(child, word, depth + 1))
                return true;
        }

        return false;
    }

    // The x at the right end of the box, the funnel in front of it and the count in front of that
    private void ArrangeSearchEnd(UIRect box)
    {
        float side = SearchHeight - 8.0f;
        var x = new UIRect(new Vector2(box.Max.X - side - 4.0f, box.Min.Y + 4.0f), new Vector2(box.Max.X - 4.0f, box.Max.Y - 4.0f));
        clear.ArrangeTree(x);
        float funnelEnd = clear.Visible ? x.Min.X - 2.0f : box.Max.X - 4.0f;
        var funnel = new UIRect(new Vector2(funnelEnd - side, box.Min.Y + 4.0f), new Vector2(funnelEnd, box.Max.Y - 4.0f));
        filters.ArrangeTree(funnel);
        float end = funnel.Min.X - 4.0f;
        found.ArrangeTree(new UIRect(new Vector2(MathF.Max(box.Min.X, end - FOUND_WIDTH), box.Min.Y), new Vector2(end, box.Max.Y)));
    }

    // The x pressed, the search emptied and the keyboard back in it to type something else
    private void ClearSearch()
    {
        Find(string.Empty);
        search.Focus();
    }

    /// <summary>
    /// What the funnel at the end of the search offers, the words that narrow it (@modified, @id:, @section:), and under
    /// them every change reset while a program asks first (<see cref="OnResetAllAsked"/>). The funnel shows it, a test
    /// asks for it.
    /// </summary>
    public Menu FilterMenu()
    {
        var menu = new Menu(texts.Filter);
        menu.Add(texts.FilterModified, () => AddToSearch(MODIFIED), MODIFIED);
        menu.Add(texts.FilterId, () => AddToSearch(ID), ID);
        menu.Add(texts.FilterSection, () => AddToSearch(SECTION), SECTION);
        if (common.Length > 0)
            menu.Add(texts.FilterCommon, () => AddToSearch(COMMON), COMMON);
        AddResetAll(menu);
        return menu;
    }

    // The words of a search that stand for a filter, drawn as chips in the box and taken out whole by a Backspace after them
    private static readonly string[] CHIPS = [MODIFIED, COMMON];

    // Whether @common is among what's typed, the entry at the top of the sections lit while it is
    private bool commonShown;

    /// <summary>Whether the search is leaving only the commonly used settings, @common among its words.</summary>
    public bool ShowingCommon => commonShown;

    // Backspace straight after a chip (and the space after it) takes the whole of it out, the way a chip goes in one
    // press rather than a letter at a time
    private bool SearchTakeKey(char key)
    {
        if (key != UIKeyboard.BACKSPACE || search.Selection.Length > 0)
            return false;

        string before = search.Text[..search.Caret];
        string trimmed = before.EndsWith(' ') ? before[..^1] : before;
        foreach (string chip in CHIPS)
        {
            int at = trimmed.Length - chip.Length;
            if (at < 0 || !trimmed.EndsWith(chip, StringComparison.OrdinalIgnoreCase) || (at > 0 && trimmed[at - 1] != ' '))
                continue;

            search.Select(at, search.Caret);
            search.Backspace();
            return true;
        }

        return false;
    }

    /// <summary>The chips in the search box as they were last drawn, each word and where it is. For a test.</summary>
    public IReadOnlyList<(string Word, UIRect Bounds)> SearchChips => ((SearchBox)search).Chips;

    // The search box, a chip of the accent round every filter word in it (@modified, @common) so they read as what they
    // do rather than as words to find. Only while all of it fits in the box, scrolled along a chip would be off its word
    private sealed class SearchBox : TextBox
    {
        public List<(string Word, UIRect Bounds)> Chips { get; } = [];

        protected override void Paint(UIDrawList list)
        {
            base.Paint(list);
            Chips.Clear();
            if (Text.Length == 0)
                return;

            var skin = list.Skin;
            float scale = TextScale > 0.0f ? TextScale : skin.TextScale;
            float left = Bounds.Min.X + Padding.Left, room = Bounds.Width - Padding.Left - Padding.Right;
            if (skin.Font.Measure(Text, scale, markup: false).X > room)
                return;

            int at = 0;
            foreach (string word in Text.Split(' '))
            {
                if (CHIPS.Any(chip => chip.Equals(word, StringComparison.OrdinalIgnoreCase)))
                {
                    float from = left + (at > 0 ? skin.Font.Measure(Text.AsSpan(0, at), scale, markup: false).X : 0.0f);
                    float to = left + skin.Font.Measure(Text.AsSpan(0, at + word.Length), scale, markup: false).X;
                    var chip = new UIRect(new Vector2(from - 4.0f, Bounds.Min.Y + 4.0f), new Vector2(to + 4.0f, Bounds.Max.Y - 4.0f));
                    Vector4 accent = skin.AccentColor;
                    list.RoundRect(chip, chip.Height * 0.5f, accent with { W = 0.18f });
                    list.RoundOutline(chip, chip.Height * 0.5f, 1.0f, accent with { W = 0.5f });
                    Chips.Add((word, chip));
                }

                at += word.Length + 1;
            }
        }
    }

    // Reset all settings on the end of a menu, after a rule, only while somebody's there to ask and something's changed
    private void AddResetAll(Menu menu)
    {
        if (OnResetAllAsked is not { } asked)
            return;

        menu.AddSeparator();
        menu.Add(texts.ResetAll, asked, icon: "discard").IsEnabled = () => ModifiedCount > 0;
    }

    /// <summary>How many settings on the page aren't what they come as, what Reset all settings would put back.</summary>
    public int ModifiedCount => ChangedKeys(null).Length;

    // The funnel pressed, its menu under it
    private void ShowFilters()
    {
        if (Module is { } module)
            ContextMenu.Show(module, new Vector2(filters.Bounds.Min.X, filters.Bounds.Min.Y), FilterMenu());
    }

    /// <summary>
    /// A word put on the end of the search, the way the funnel's menu does, the keyboard in the box after it to go on
    /// typing (the key after an @id:). One that's there already isn't put twice.
    /// </summary>
    public void AddToSearch(string word)
    {
        string typed = search.Text.TrimEnd();
        bool there = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(already => already.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (!there)
            typed = typed.Length == 0 ? word : $"{typed} {word}";

        // @id: wants its key typed straight after it, the rest a space to type the next word
        if (!word.EndsWith(':'))
            typed += " ";
        Find(typed);
        search.Focus();
    }

    /// <summary>
    /// What Escape does on the page. With something in the search it's emptied and the page stays, with nothing in it the
    /// page closes. True when it did either. It closed at once whatever was typed, and a search typed wrong took the whole
    /// window with it.
    /// </summary>
    public bool Escape()
    {
        if (search.Text.Length > 0)
        {
            Find(string.Empty);
            return true;
        }

        if (onClose is null)
            return false;

        onClose();
        return true;
    }

    protected internal override void OnKeyDown(in UIKeyPress press)
    {
        if (press.Key == Key.Escape)
            Escape();
    }

    // Escape in the search box, or anything else of the page with the keyboard
    protected internal override bool OnEscape() => Escape();
}
