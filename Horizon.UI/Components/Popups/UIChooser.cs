using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;

namespace Horizon.UI.Components;

/// <summary>
/// One of a list picked in a palette, narrowed down by what is typed (a piece of it, or the letters starting its words),
/// enter or a click on the chosen row picks and escape goes. A command palette and every chooser of a program, Hex's quick
/// open with its prefixes (> for commands, @ for something in the file) among them. It's a card hanging under the top of
/// the window, Spotlight's, a box to type in with the title written in it until something is, the list under a hairline,
/// and the line about what can be typed quietly at the bottom. A choice written <c>"Menu: What it does   (Ctrl+K)"</c> is shown
/// with the menu quiet in front and the keys as keycaps along the right.
/// <para>
/// Given a <see cref="UIRecentList"/> the ones picked last go over the rest (the ones that are still choices and match
/// what is typed), thinner and a colour of their own (<see cref="ListBox.Lead"/>), and what is picked is remembered in
/// it. Only for the list the dialog started with, a prefix's list is a list of its own.
/// </para>
/// </summary>
public static partial class UIChooser
{
    private const float WIDTH = 640.0f, BOX_HEIGHT = 40.0f, BOX_TEXT = 0.18f, TEXT = 0.15f, HINT_TEXT = 0.13f;

    // How far in from the card's edge the line along the bottom is written, where the rows' text starts
    private const float HINT_INSET = 12.0f;
    private const int ROWS = 12, MOST = 300;

    /// <param name="shortestFirst">Whether what is left is put shortest first, the nearest to what was typed for a file's name.</param>
    /// <param name="prefixed">
    /// Other lists for what is typed starting with a character of their own, null for the list the dialog started with.
    /// The rest of what is typed narrows them down.
    /// </param>
    /// <param name="typed">What's in the box as it opens, ":" for the lines of a file, the way VS Code's Ctrl+G opens.</param>
    public static UIDialog Show(UIModule module, string title, string message, List<(string Text, Action Picked)> choices,
        string placeholder = "a few letters", bool shortestFirst = false, Func<char, List<(string Text, Action Picked)>?>? prefixed = null,
        UIRecentList? recent = null, string typed = "") =>
        Show(module, title, message, Rows(choices), placeholder, shortestFirst,
            prefixed is null ? null : prefix => prefixed(prefix) is { } some ? Rows(some) : null, recent, typed);

    /// <summary>Rows written the old way, <c>"File: Save   (Ctrl+S)"</c>, as rows in their parts (<see cref="UIChoice.Of"/>).</summary>
    public static List<UIChoice> Rows(List<(string Text, Action Picked)> choices) => [.. choices.Select(choice => UIChoice.Of(choice.Text, choice.Picked))];

    /// <summary>
    /// The chooser of rows in their parts, the menu quiet in front, a symbol, a folder or an author dim after, keys as
    /// keycaps, and the letters typed lit up in the accent. Rows are told apart, narrowed down and remembered by their
    /// <see cref="UIChoice.Id"/>.
    /// </summary>
    public static UIDialog Show(UIModule module, string title, string message, List<UIChoice> choices,
        string placeholder = "a few letters", bool shortestFirst = false, Func<char, List<UIChoice>?>? prefixed = null,
        UIRecentList? recent = null, string typed = "")
    {
        string[] texts = [];
        var byText = new Dictionary<string, UIChoice>();
        char showing = '￿';
        ListBox list = null!;
        Label nothing = null!;

        // The list that is being narrowed down, the one the dialog started with or another for a prefix
        void Use(char prefix, List<UIChoice> from)
        {
            showing = prefix;
            byText.Clear();
            foreach (UIChoice choice in from)
                byText.TryAdd(choice.Id, choice);
            texts = [.. byText.Keys];
            list.IconRoom = byText.Values.Any(choice => choice.Icon.Length > 0);
        }

        void PickChosen()
        {
            if (list.Index < 0 || list.Index >= list.Items.Length || !byText.TryGetValue(list.Items[list.Index], out UIChoice? picked))
                return;

            // Remembered before it runs, what it runs might well open another chooser
            if (showing == '\0')
                recent?.Remember(list.Items[list.Index]);
            picked.Picked();
        }

        // What is left for what was typed, the ones picked last first when it's the list the dialog started with
        void Narrow(string query)
        {
            // The best matches first, what starts with what was typed, then what has a word starting with it, then what has
            // it whole, then the first letters of its words, then the letters in a few pieces. Dusk's "dawn" had Dawn, the
            // editor under a command that only had the letters
            var ranked = texts.Where(text => Matches(text, query)).Select(text => (Text: text, Rank: Rank(text, query))).ToList();

            // The letters only in a few pieces are kept for when nothing has more of it than that, "close" listed
            // Trigger Completion under Close and Close All
            int best = ranked.Count > 0 ? ranked.Min(pair => pair.Rank) : 0;
            var ordered = ranked.Where(pair => best >= LOOSE || pair.Rank < LOOSE).OrderBy(pair => pair.Rank);

            // Files, a backup or a picture after the code as good as it, a short a.bak sat over Program.cs
            if (shortestFirst && showing == '\0')
                ordered = ordered.ThenBy(pair => Leftover(pair.Text)).ThenBy(pair => Path.GetFileName(pair.Text).Length);

            // the shorter one goes first between two as good, nothing typed keeps the list's own order
            if (query.Trim().Length > 0)
                ordered = ordered.ThenBy(pair => Columns(pair.Text).Text.Length);
            var left = ordered.Select(pair => pair.Text);

            string[] lead = showing == '\0' && recent is not null
                ? [.. recent.Items.Where(text => byText.ContainsKey(text) && Matches(text, query))]
                : [];
            list.Lead = lead.Length;
            list.Query = query;
            list.Items =lead.Length == 0 ? [.. left.Take(MOST)] : [.. lead, .. left.Where(text => !lead.Contains(text)).Take(MOST)];
            if (list.Items.Length > 0) list.Choose(0);

            // The card as tall as what's left and no taller, Spotlight's, and a line saying so when nothing is
            list.Rows = Math.Clamp(list.Items.Length, 1, ROWS);
            list.Visible = list.Items.Length > 0;
            nothing.Visible = !list.Visible;
            nothing.Text = query.Trim().Length > 0 ? $"Nothing matches \"{query.Trim()}\"" : "Nothing to pick from here";
        }

        // The title is what the box says until something's typed, the line about it goes quietly along the bottom
        UIDialog shown = UIDialog.ShowPalette(module, title, new DialogChoice("Go", PickChosen), new DialogChoice("Cancel"));
        Vector4 edge = module.Compositor.Skin?.BorderColor ?? new Vector4(1.0f, 1.0f, 1.0f, 0.1f);
        var picking = shown.Body.Add(new StackPanel { Spacing = 6.0f });
        TextBox box = picking.Add(new TextBox
        {
            Size = new Vector2(WIDTH, BOX_HEIGHT),
            Padding = new UIEdges(10.0f, 6.0f, 10.0f, 6.0f),
            Placeholder = title.Length > 0 ? title : placeholder,
            TextScale = BOX_TEXT,
            MaxLength = 400,
            Bare = true,
            LeadIcon = "search"
        });
        picking.Add(new Panel { Size = new Vector2(WIDTH, 1.0f), Color = edge });
        list = picking.Add(new ListBox { Size = new Vector2(WIDTH, 0.0f), Rows = ROWS, TextScale = TEXT, Plain = true, Choice = id => byText.GetValueOrDefault(id) });
        nothing = picking.Add(new Label { TextScale = TEXT, Align = Origin.Center, Size = new Vector2(WIDTH, 64.0f), Visible = false, Color = module.Compositor.Skin?.DimTextColor });
        // The line along the bottom in from the card's edge as far as the rows' text is. A label writes over the whole of its
        // bounds, its padding only made it bigger, and the line sat hard against the edge of the glass
        if (message.Length > 0)
        {
            Panel foot = picking.Add(new Panel { Size = new Vector2(WIDTH, 0.0f), Padding = new UIEdges(HINT_INSET, 4.0f, HINT_INSET, 2.0f), Anchor = Origin.Left });
            foot.Add(new Label { Text = message, TextScale = HINT_TEXT, Wrap = true, Size = new Vector2(WIDTH - HINT_INSET * 2.0f, 0.0f), Align = Origin.TopLeft, Color = module.Compositor.Skin?.DimTextColor });
        }

        Use('\0', choices);
        Narrow(string.Empty);

        box.OnChanged = typed =>
        {
            char prefix = typed.Length > 0 && prefixed?.Invoke(typed[0]) is not null ? typed[0] : '\0';
            if (prefix != showing)
                Use(prefix, prefix == '\0' ? choices : prefixed!(prefix)!);

            Narrow(prefix == '\0' ? typed : typed[1..]);
        };
        box.OnSubmitted = _ =>
        {
            shown.Close();
            PickChosen();
        };

        // Started with something typed, it's narrowed down to that at once
        if (typed.Length > 0)
        {
            box.Text = typed;
            box.OnChanged(typed);
        }

        // The arrows and the pages go up and down the list while the box has the keyboard
        box.OnKey = key =>
        {
            int step = key switch
            {
                UIKeyboard.UP or UIKeyboard.SELECT_UP => -1,
                UIKeyboard.DOWN or UIKeyboard.SELECT_DOWN => 1,
                UIKeyboard.PAGE_UP or UIKeyboard.SELECT_PAGE_UP => -Math.Max(1, list.Rows - 1),
                UIKeyboard.PAGE_DOWN or UIKeyboard.SELECT_PAGE_DOWN => Math.Max(1, list.Rows - 1),
                _ => 0
            };
            if (step != 0 && list.Items.Length > 0)
                list.Choose(Math.Clamp(list.Index + step, 0, list.Items.Length - 1));
        };
        list.OnActivated = _ =>
        {
            shown.Close();
            PickChosen();
        };

        box.Focus();
        return shown;
    }

    /// <summary>
    /// A choice in the three parts the list shows, <c>"File: Save   (Ctrl+S)"</c> as File quiet in front, Save, and Ctrl+S as
    /// keycaps. A path isn't taken for a menu, the colon after a drive letter has no space after it.
    /// </summary>
    public static (string Quiet, string Text, string Keys) Columns(string text)
    {
        string keys = string.Empty;
        int open = text.LastIndexOf("   (", StringComparison.Ordinal);
        if (open > 0 && text.EndsWith(')'))
        {
            keys = text[(open + 4)..^1];
            text = text[..open];
        }

        string quiet = string.Empty;
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && colon <= 32 && text.AsSpan(0, colon).IndexOfAny('/', '\\') < 0)
        {
            quiet = text[..colon];
            text = text[(colon + 2)..];
        }

        return (quiet, text, keys);
    }

    // The rank of a choice that only has the letters typed in a few pieces each starting a word, see Rank
    private const int LOOSE = 5;

    // How many pieces the letters can be in before it's just letters scattered about. "stop" ran Selection Move Line Up
    private const int MOST_RUNS = 3;

    /// <summary>
    /// How well a choice matches what was typed, lower better. 0 when what it does starts with it, 1 when a word of it
    /// does, 2 when it's in there whole, 3 when the menu it's in has it, 4 when the letters start its words, 5 for the
    /// letters in a few pieces that start words.
    /// </summary>
    public static int Rank(string text, string typed)
    {
        string query = typed.Trim();
        if (query.Length == 0)
            return 0;

        string said = Columns(text).Text;
        if (said.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 0;

        for (int at = said.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = said.IndexOf(query, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (at == 0 || !char.IsLetterOrDigit(said[at - 1]))
                return 1;
        }

        if (said.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        return Initials(text, query) ? 4 : LOOSE;
    }

    /// <summary>
    /// Whether the text has what was typed in it, or the letters typed each start a word of it ("ssl" for Select Show
    /// Lines), or the letters are in it in a few pieces with every piece after the first starting a word.
    /// </summary>
    public static bool Matches(string text, string typed)
    {
        string query = typed.Trim();
        if (query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return Initials(text, query) || FewRuns(text, query);
    }

    // Whether a word of the text starts at that spot, after a space or a separator or where camel case goes up a letter
    private static bool WordStart(string text, int at)
    {
        if (at == 0)
            return true;

        char before = text[at - 1];
        return before is ' ' or '/' or '\\' or '.' or '_' or '-' or ':' or '(' || (char.IsLower(before) && char.IsUpper(text[at]));
    }

    private static bool Same(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    // Whether every letter typed lands on the start of a word, in order, spaces typed don't count
    private static bool Initials(string text, string typed)
    {
        int at = 0;
        foreach (char letter in typed)
        {
            if (char.IsWhiteSpace(letter))
                continue;

            while (at < text.Length && !(Same(text[at], letter) && WordStart(text, at)))
                at++;
            if (at == text.Length)
                return false;
            at++;
        }

        return true;
    }

    // Whether the letters typed are in the text in order in no more than MOST_RUNS pieces, the first anywhere and the
    // rest each starting a word. runs[t] is the fewest pieces it takes with the letter so far landing at t
    private static bool FewRuns(string text, string typed)
    {
        string letters = string.Concat(typed.Where(letter => !char.IsWhiteSpace(letter)));
        if (letters.Length == 0)
            return true;
        if (letters.Length > text.Length)
            return false;

        const int NONE = int.MaxValue / 2;
        var runs = new int[text.Length];
        var next = new int[text.Length];
        for (int t = 0; t < text.Length; t++)
            runs[t] = Same(text[t], letters[0]) ? 1 : NONE;

        for (int i = 1; i < letters.Length; i++)
        {
            // the fewest pieces for the last letter anywhere before t, for a new piece starting at t
            int fewest = NONE;
            for (int t = 0; t < text.Length; t++)
            {
                int best = NONE;
                if (Same(text[t], letters[i]))
                {
                    if (t > 0)
                        best = runs[t - 1];
                    if (WordStart(text, t))
                        best = Math.Min(best, fewest + 1);
                }

                next[t] = best;
                fewest = Math.Min(fewest, runs[t]);
            }

            (runs, next) = (next, runs);
        }

        return runs.Min() <= MOST_RUNS;
    }
}
