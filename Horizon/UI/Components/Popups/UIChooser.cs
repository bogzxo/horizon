using System.Numerics;

using Horizon.UI;

namespace Horizon.UI.Components;

/// <summary>
/// One of a list picked in a dialog, narrowed down by what is typed (the letters in order, the way quick open goes),
/// enter, the first button or a double click picks. A command palette and every chooser of a program, Hex's quick open
/// with its prefixes (> for commands, @ for something in the file) among them.
/// <para>
/// Given a <see cref="UIRecentList"/> the ones picked last go over the rest (the ones that are still choices and match
/// what is typed), thinner and a colour of their own (<see cref="ListBox.Lead"/>), and what is picked is remembered in
/// it. Only for the list the dialog started with, a prefix's list is a list of its own.
/// </para>
/// </summary>
public static class UIChooser
{
    private const float WIDTH = 620.0f, BOX_HEIGHT = 32.0f, TEXT = 0.15f;
    private const int ROWS = 14, MOST = 300;

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
            var left = texts.Where(text => Matches(text, query));
            if (shortestFirst && showing == '\0')
                left = left.OrderBy(text => Path.GetFileName(text).Length);

            string[] lead = showing == '\0' && recent is not null
                ? [.. recent.Items.Where(text => byText.ContainsKey(text) && Matches(text, query))]
                : [];
            list.Lead = lead.Length;
            list.Items = lead.Length == 0 ? [.. left.Take(MOST)] : [.. lead, .. left.Where(text => !lead.Contains(text)).Take(MOST)];
            if (list.Items.Length > 0) list.Choose(0);
        }

        UIDialog shown = UIDialog.Show(module, title, message, new DialogChoice("Go", PickChosen), new DialogChoice("Cancel"));
        var picking = shown.Body.Add(new StackPanel { Spacing = 6.0f });
        TextBox box = picking.Add(new TextBox { Size = new Vector2(WIDTH, BOX_HEIGHT), Padding = new UIEdges(8.0f, 6.0f, 8.0f, 6.0f), Placeholder = placeholder, TextScale = TEXT, MaxLength = 400 });
        list = picking.Add(new ListBox { Size = new Vector2(WIDTH, 0.0f), Rows = ROWS, TextScale = TEXT });

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
