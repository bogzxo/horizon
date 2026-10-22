using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;

namespace Horizon.UI.Components;

/// <summary>
/// One of a list picked in a palette, narrowed down by what is typed (the letters in order, the way quick open goes),
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
public static class UIChooser
{
    private const float WIDTH = 640.0f, BOX_HEIGHT = 40.0f, BOX_TEXT = 0.18f, TEXT = 0.15f, HINT_TEXT = 0.13f;
    private const int ROWS = 12, MOST = 300;

    /// <param name="shortestFirst">Whether what is left is put shortest first, the nearest to what was typed for a file's name.</param>
    /// <param name="prefixed">
    /// Other lists for what is typed starting with a character of their own, null for the list the dialog started with.
    /// The rest of what is typed narrows them down.
    /// </param>
    public static UIDialog Show(UIModule module, string title, string message, List<(string Text, Action Picked)> choices,
        string placeholder = "a few letters", bool shortestFirst = false, Func<char, List<(string Text, Action Picked)>?>? prefixed = null,
        UIRecentList? recent = null)
    {
        string[] texts = [];
        var byText = new Dictionary<string, Action>();
        char showing = '￿';

        // The list that is being narrowed down, the one the dialog started with or another for a prefix
        void Use(char prefix, List<(string Text, Action Picked)> from)
        {
            showing = prefix;
            texts = [.. from.Select(choice => choice.Text)];
            byText.Clear();
            foreach (var (text, picked) in from)
                byText.TryAdd(text, picked);
        }

        Use('\0', choices);

        ListBox list = null!;
        Label nothing = null!;
        void PickChosen()
        {
            if (list.Index < 0 || list.Index >= list.Items.Length || !byText.TryGetValue(list.Items[list.Index], out Action? picked))
                return;

            // Remembered before it runs, what it runs might well open another chooser
            if (showing == '\0')
                recent?.Remember(list.Items[list.Index]);
            picked();
        }

        // What is left for what was typed, the ones picked last first when it's the list the dialog started with
        void Narrow(string query)
        {
            // The best matches first, what starts with what was typed, then what has a word starting with it, then what has
            // it whole, then the letters in order. Dusk's "dawn" had Dawn, the editor under a command that only had the letters
            var left = texts.Where(text => Matches(text, query)).OrderBy(text => Rank(text, query));
            if (shortestFirst && showing == '\0')
                left = left.ThenBy(text => Path.GetFileName(text).Length);

            string[] lead = showing == '\0' && recent is not null
                ? [.. recent.Items.Where(text => byText.ContainsKey(text) && Matches(text, query))]
                : [];
            list.Lead = lead.Length;
            list.Items = lead.Length == 0 ? [.. left.Take(MOST)] : [.. lead, .. left.Where(text => !lead.Contains(text)).Take(MOST)];
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
        list = picking.Add(new ListBox { Size = new Vector2(WIDTH, 0.0f), Rows = ROWS, TextScale = TEXT, Plain = true, Columns = Columns });
        nothing = picking.Add(new Label { TextScale = TEXT, Align = Origin.Center, Size = new Vector2(WIDTH, 64.0f), Visible = false, Color = module.Compositor.Skin?.DimTextColor });
        if (message.Length > 0)
            picking.Add(new Label { Text = message, TextScale = HINT_TEXT, Wrap = true, Size = new Vector2(WIDTH - 20.0f, 0.0f), Align = Origin.TopLeft, Anchor = Origin.Left, Padding = new UIEdges(10.0f, 4.0f, 10.0f, 2.0f), Color = module.Compositor.Skin?.DimTextColor });

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

    /// <summary>
    /// How well a choice matches what was typed, lower better. 0 when what it does starts with it, 1 when a word of it
    /// does, 2 when it's in there whole, 3 when the menu it's in has it, 4 for the letters only in that order.
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
        return text.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : 4;
    }

    /// <summary>Whether the letters typed are in the text in that order, the way quick open matches.</summary>
    public static bool Matches(string text, string typed)
    {
        int at = 0;
        foreach (char letter in typed)
        {
            if (char.IsWhiteSpace(letter))
                continue;

            at = text.IndexOf(letter.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;
            at++;
        }

        return true;
    }
}
