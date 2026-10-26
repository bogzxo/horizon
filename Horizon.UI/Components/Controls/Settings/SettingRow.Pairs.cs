using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// A row of pairs, an object of texts by text (files_associations the first of them), the way VS Code edits one. A line a
// pair under the sentence, its key and its value in two columns with a pencil and a bin at its end, and Add Item under
// them. The pencil turns its line into two boxes, Enter puts it in and Escape leaves it as it was, Add Item does the same
// with a new line at the end. Up and Down go along the buttons, and on to the rows either side past the first and last
internal sealed partial class SettingRow
{
    // What the row has now, which line is being typed (as many as there are for a new one, -1 for none) and its boxes
    private List<(string Key, string Value)> pairs = [];
    private int editingPair = -1;
    private PairBox? keyBox, valueBox;

    // Where the lines go, how wide they are, the pencil of every line and Add Item, so the keyboard can be put back on
    // one after the lines are made again
    private StackPanel? pairList;
    private float pairsWidth;
    private readonly List<Button> pairEdits = [], pairRemoves = [];
    private Button? addItem;

    // Every key and value written out, what a search finds the row by besides its name and sentence
    private string pairsText = string.Empty;

    private Action<IRuntimeValue?> Pairs(UISetting setting, float width)
    {
        pairsWidth = width;
        pairList = Add(new StackPanel { Spacing = 2.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        faded.Add(pairList);
        BuildPairs();
        return value =>
        {
            var now = UIFormPairs.Of(value);
            pairsText = string.Join('\n', now.Select(pair => $"{pair.Key} {pair.Value}"));

            // A line being typed is left alone, it's made again once it's put in or left
            if (editingPair >= 0 || now.SequenceEqual(pairs))
                return;
            pairs = now;
            BuildPairs();
        };
    }

    // The lines made afresh from what the row has, the one being typed as its two boxes
    private void BuildPairs()
    {
        if (pairList is null)
            return;

        foreach (UIComponent child in pairList.Children.ToArray())
            pairList.Remove(child);
        pairEdits.Clear();
        pairRemoves.Clear();
        keyBox = valueBox = null;

        float tall = page.RowControlHeight;
        float side = tall;
        float column = MathF.Floor((pairsWidth - side * 2.0f - 8.0f * 3.0f) * 0.5f);

        // The two columns' headings, only while there's something under them
        if (pairs.Count > 0 || editingPair >= 0)
        {
            var heading = pairList.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
            heading.Add(new Label { Text = $"[color:dim]{UIRichText.Literal(page.Texts.PairKey)}[/color]", TextScale = page.RowBodyScale * 0.85f, Align = Origin.Left, Size = new Vector2(column, tall * 0.8f) });
            heading.Add(new Label { Text = $"[color:dim]{UIRichText.Literal(page.Texts.PairValue)}[/color]", TextScale = page.RowBodyScale * 0.85f, Align = Origin.Left, Size = new Vector2(column, tall * 0.8f) });
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            if (i == editingPair)
                PairBoxes(column, pairs[i].Key, pairs[i].Value);
            else
                PairLine(i, column, side);
        }

        if (editingPair >= pairs.Count)
            PairBoxes(column, string.Empty, string.Empty);

        // Add Item under them, not while a line is being typed, Enter or OK is how that one goes in
        addItem = null;
        if (editingPair < 0)
        {
            float wide = MathF.Max(110.0f * page.ZoomNow, page.Texts.AddItem.Length * page.RowBodyScale * 0.6f + side + 24.0f);
            addItem = pairList.Add(new PairButton(this)
            {
                Label = page.Texts.AddItem,
                Icon = "add",
                Size = new Vector2(wide, tall),
                LabelScale = page.RowBodyScale,
                Animated = false,
                Anchor = Origin.Left,
                Pivot = Origin.Left,
                OnPressed = AddPair
            });
        }
    }

    // A pair as it is, its key and its value with its pencil and its bin
    private void PairLine(int at, float column, float side)
    {
        var line = pairList!.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        float tall = page.RowControlHeight;
        line.Add(new Label { Text = UIRichText.Literal(pairs[at].Key), TextScale = page.RowBodyScale, Align = Origin.Left, Size = new Vector2(column, tall) });
        line.Add(new Label { Text = UIRichText.Literal(pairs[at].Value), TextScale = page.RowBodyScale, Align = Origin.Left, Size = new Vector2(column, tall) });
        pairEdits.Add(line.Add(new PairButton(this) { Icon = "edit", Size = new Vector2(side, side), LabelScale = page.RowBodyScale, Style = "button_flat", Animated = false, Tooltip = page.Texts.EditItem, OnPressed = () => EditPair(at) }));
        pairRemoves.Add(line.Add(new PairButton(this) { Icon = "trash", Size = new Vector2(side, side), LabelScale = page.RowBodyScale, Style = "button_flat", Animated = false, Tooltip = page.Texts.RemoveItem, OnPressed = () => RemovePair(at) }));
    }

    // The line being typed, a box for its key and one for its value with what one looks like in each, then OK and Cancel
    private void PairBoxes(float column, string key, string value)
    {
        var line = pairList!.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        float tall = page.RowControlHeight;
        keyBox = line.Add(new PairBox(this) { Text = key, Placeholder = Setting.KeyPlaceholder, Size = new Vector2(column, tall), TextScale = page.RowBodyScale, MaxLength = 256 });
        valueBox = line.Add(new PairBox(this) { Text = value, Placeholder = Setting.ValuePlaceholder, Size = new Vector2(column, tall), TextScale = page.RowBodyScale, MaxLength = 256 });
        keyBox.TakeKey = valueBox.TakeKey = PairKey;
        float wide = MathF.Max(64.0f * page.ZoomNow, page.Texts.PairCancel.Length * page.RowBodyScale * 0.6f + 20.0f);
        line.Add(new PairButton(this) { Label = page.Texts.PairOk, Size = new Vector2(wide, tall), LabelScale = page.RowBodyScale, Animated = false, OnPressed = CommitPair });
        line.Add(new PairButton(this) { Label = page.Texts.PairCancel, Size = new Vector2(wide, tall), LabelScale = page.RowBodyScale, Style = "button_flat", Animated = false, OnPressed = CancelPair });
    }

    // Enter puts the line in and Escape leaves it, Up and Down stay in the box rather than leaving a line half typed
    private bool PairKey(char key)
    {
        switch (key)
        {
            case UIKeyboard.ENTER:
                CommitPair();
                return true;
            case UIKeyboard.ESCAPE:
                CancelPair();
                return true;
            case UIKeyboard.UP or UIKeyboard.DOWN:
                return true;
        }

        return false;
    }

    // Add Item, a new line of two empty boxes at the end with the keyboard in the first
    private void AddPair()
    {
        editingPair = pairs.Count;
        BuildPairs();
        keyBox?.Focus();
    }

    // A pair's pencil, its line two boxes with the keyboard in the key's, all of it picked out to type over
    private void EditPair(int at)
    {
        if (at < 0 || at >= pairs.Count)
            return;

        editingPair = at;
        BuildPairs();
        keyBox?.Focus();
        keyBox?.SelectAll();
    }

    // The line being typed put in, a key that's there already on another line taking that line's place. A line with no
    // key goes nowhere, the same as leaving it
    private void CommitPair()
    {
        if (editingPair < 0 || keyBox is null || valueBox is null)
            return;

        string key = keyBox.Text.Trim();
        if (key.Length == 0)
        {
            CancelPair();
            return;
        }

        var next = new List<(string Key, string Value)>(pairs);
        (string, string) typed = (key, valueBox.Text.Trim());
        if (editingPair < next.Count)
            next[editingPair] = typed;
        else
            next.Add(typed);

        int kept = editingPair < pairs.Count ? editingPair : next.Count - 1;
        next = [.. next.Where((pair, at) => at == kept || pair.Key != key)];
        pairs = next;
        editingPair = -1;
        BuildPairs();
        FocusPairLine(next.FindIndex(pair => pair.Key == key));
        page.Changed(Setting.Key, UIFormPairs.Value(pairs));
    }

    // The line being typed left as it was, the keyboard back on its pencil (or Add Item for a new one)
    private void CancelPair()
    {
        if (editingPair < 0)
            return;

        int was = editingPair;
        editingPair = -1;
        BuildPairs();
        FocusPairLine(was);
    }

    // A pair's bin, the keyboard on the line that took its place, or Add Item when it was the last
    private void RemovePair(int at)
    {
        if (at < 0 || at >= pairs.Count)
            return;

        pairs = [.. pairs.Where((_, index) => index != at)];
        editingPair = -1;
        BuildPairs();
        FocusPairLine(at);
        page.Changed(Setting.Key, UIFormPairs.Value(pairs));
    }

    // The keyboard on a line's pencil, Add Item past the last line
    private void FocusPairLine(int at)
    {
        if (at >= 0 && at < pairEdits.Count)
            pairEdits[at].Focus();
        else
            addItem?.Focus();
    }

    // Up and Down from one of the pairs' buttons, the one before or after it, false past either end for the page to go
    // on to the next row
    private bool MoveInPairs(UIComponent from, int delta)
    {
        if (pairList is null)
            return false;

        var stops = new List<UIComponent>();
        UIModule.CollectFocusable(pairList, stops);
        int at = stops.IndexOf(from);
        int to = at + delta;
        if (at < 0 || to < 0 || to >= stops.Count)
            return false;

        stops[to].Focus();
        return true;
    }

    /// <summary>What a row of pairs shows, the line being typed as it was before, empty for any other row.</summary>
    public IReadOnlyList<(string Key, string Value)> PairsShown => pairs;

    /// <summary>The line being typed, as many as there are for a new one, -1 for none.</summary>
    public int EditingPair => editingPair;

    /// <summary>The boxes of the line being typed, nulls while none is.</summary>
    public (TextBox? Key, TextBox? Value) PairBoxesShown => (keyBox, valueBox);

    /// <summary>Where Add Item is, and every line's pencil and bin, for a test to press with the pointer.</summary>
    public (UIRect AddItem, UIRect[] Edits, UIRect[] Removes) PairBounds =>
        (addItem?.Bounds ?? default, [.. pairEdits.Select(button => button.Bounds)], [.. pairRemoves.Select(button => button.Bounds)]);

    /// <summary>Whether a word is in one of the keys or values of a row of pairs, what a search finds it by.</summary>
    public bool PairsMatch(string word) => pairsText.Length > 0 && pairsText.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first pair a word is in, "*.orig = csharp", for the pill after the key, null for none.</summary>
    public string? PairFoundBy(string word)
    {
        foreach (var (key, value) in UIFormPairs.Of(page.ValueOf?.Invoke(Setting.Key)))
        {
            if (key.Contains(word, StringComparison.OrdinalIgnoreCase) || value.Contains(word, StringComparison.OrdinalIgnoreCase))
                return $"{key} = {value}";
        }

        return null;
    }

    // A box of the line being typed, Escape leaves the line rather than the page
    private sealed class PairBox(SettingRow row) : TextBox
    {
        protected internal override bool OnEscape()
        {
            row.CancelPair();
            return true;
        }
    }

    // One of the pairs' buttons, Up and Down along them before they go row to row
    private sealed class PairButton(SettingRow row) : Button
    {
        protected internal override void OnTextInput(char character)
        {
            if (character is UIKeyboard.UP or UIKeyboard.DOWN && row.MoveInPairs(this, character == UIKeyboard.DOWN ? 1 : -1))
                return;

            if (!row.Offer(this, character))
                base.OnTextInput(character);
        }
    }
}
