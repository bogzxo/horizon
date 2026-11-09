using System.Numerics;

using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI.Drawing;

namespace Horizon.UI.Components;

// A row of a list of texts (files_exclude and search_exclude the first of them), the way VS Code edits one. The same look
// as a row of pairs with one column, a line an item with a pencil and a bin at its end and a button under them that
// says what it adds (the setting's ValuePlaceholder, Add Pattern, or Add Item for one that doesn't say). The pencil turns
// its line into a box with what one looks like in it (KeyPlaceholder), Enter puts it in and Escape leaves it, Delete on
// a line's buttons takes it out, and Up and Down go along the buttons and on to the rows either side
internal sealed partial class SettingRow
{
    // What the row has now, which line is being typed (as many as there are for a new one, -1 for none) and its box
    private List<string> items = [];
    private int editingItem = -1;
    private PairBox? itemBox;

    // Where the lines go, how wide they are, every line's pencil and bin and the add button
    private StackPanel? itemList;
    private float itemsWidth;
    private readonly List<Button> itemEdits = [], itemRemoves = [];
    private Button? addListItem;

    private Action<IRuntimeValue?> ListOf(UISetting setting, float width)
    {
        itemsWidth = width;
        itemList = Add(new StackPanel { Spacing = 2.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        faded.Add(itemList);
        BuildItems();
        return value =>
        {
            var now = UIFormList.Of(value);

            // Searched the same way as the pairs' keys and values, see PairsMatch
            pairsText = string.Join('\n', now);

            // A line being typed is left alone, it's made again once it's put in or left
            if (editingItem >= 0 || now.SequenceEqual(items))
                return;
            items = now;
            BuildItems();
        };
    }

    // The lines made afresh from what the row has, the one being typed as its box
    private void BuildItems()
    {
        if (itemList is null)
            return;

        foreach (UIComponent child in itemList.Children.ToArray())
            itemList.Remove(child);
        itemEdits.Clear();
        itemRemoves.Clear();
        itemBox = null;

        float tall = page.RowControlHeight;
        float side = tall;
        float column = MathF.Floor(itemsWidth - side * 2.0f - 8.0f * 2.0f);

        // Its one heading while there's something under it, and an example dim in its place while there's nothing
        if (items.Count > 0 || editingItem >= 0)
            itemList.Add(new Label { Text = $"[color:dim]{UIRichText.Literal(page.Texts.PairKey)}[/color]", TextScale = page.RowBodyScale * 0.85f, Align = Origin.Left, Anchor = Origin.Left, Pivot = Origin.Left, Size = new Vector2(column, tall * 0.8f) });
        else if (Setting.KeyPlaceholder.Length > 0)
            itemList.Add(ExampleLine(UIRichText.Literal(Setting.KeyPlaceholder), column));

        for (int i = 0; i < items.Count; i++)
        {
            if (i == editingItem)
                ItemBox(column, items[i]);
            else
                ItemLine(i, column, side);
        }

        if (editingItem >= items.Count)
            ItemBox(column, string.Empty);

        // The add button under them, not while a line is being typed, Enter or OK is how that one goes in
        addListItem = null;
        if (editingItem < 0)
        {
            string label = Setting.ValuePlaceholder.Length > 0 ? Setting.ValuePlaceholder : page.Texts.AddItem;
            float wide = MathF.Max(110.0f * page.ZoomNow, label.Length * page.RowBodyScale * 0.6f + side + 24.0f);
            addListItem = itemList.Add(new PairButton(this)
            {
                Label = label,
                Icon = "add",
                Size = new Vector2(wide, tall),
                LabelScale = page.RowBodyScale,
                Animated = false,
                Anchor = Origin.Left,
                Pivot = Origin.Left,
                OnPressed = AddItem
            });
        }
    }

    // An item as it is with its pencil and its bin, Delete on either takes it out
    private void ItemLine(int at, float column, float side)
    {
        var line = itemList!.Add(new FocusLine { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        line.Add(new Label { Text = UIRichText.Literal(items[at]), TextScale = page.RowBodyScale, Align = Origin.Left, Size = new Vector2(column, page.RowControlHeight) });
        itemEdits.Add(line.Add(new PairButton(this) { Icon = "edit", Size = new Vector2(side, side), LabelScale = page.RowBodyScale, Style = "button_flat", Animated = false, Tooltip = page.Texts.EditItem, OnPressed = () => EditItem(at), OnDelete = () => RemoveItem(at) }));
        itemRemoves.Add(line.Add(new PairButton(this) { Icon = "trash", Size = new Vector2(side, side), LabelScale = page.RowBodyScale, Style = "button_flat", Animated = false, Tooltip = page.Texts.RemoveItem, OnPressed = () => RemoveItem(at), OnDelete = () => RemoveItem(at) }));
    }

    // The line being typed, a box with what one looks like in it, then Cancel and OK
    private void ItemBox(float column, string item)
    {
        var line = itemList!.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Anchor = Origin.Left, Pivot = Origin.Left });
        float tall = page.RowControlHeight;
        float wide = MathF.Max(64.0f * page.ZoomNow, page.Texts.PairCancel.Length * page.RowBodyScale * 0.6f + 20.0f);
        itemBox = line.Add(new PairBox(CancelItem) { Text = item, Placeholder = Setting.KeyPlaceholder, Size = new Vector2(MathF.Max(120.0f, column - wide * 2.0f + tall * 2.0f), tall), TextScale = page.RowBodyScale, MaxLength = 512 });
        itemBox.TakeKey = ItemKey;

        // Cancel then OK, the one Enter presses last and in the accent, the way every dialog has them
        line.Add(new PairButton(this) { Label = page.Texts.PairCancel, Size = new Vector2(wide, tall), LabelScale = page.RowBodyScale, Animated = false, OnPressed = CancelItem });
        line.Add(new PairButton(this) { Label = page.Texts.PairOk, Size = new Vector2(wide, tall), LabelScale = page.RowBodyScale, Primary = true, Animated = false, OnPressed = CommitItem });
    }

    // Enter puts the line in and Escape leaves it, Up and Down stay in the box rather than leaving a line half typed
    private bool ItemKey(char key)
    {
        switch (key)
        {
            case UIKeyboard.ENTER:
                CommitItem();
                return true;
            case UIKeyboard.ESCAPE:
                CancelItem();
                return true;
            case UIKeyboard.UP or UIKeyboard.DOWN:
                return true;
        }

        return false;
    }

    // The add button, a new line at the end with the keyboard in its box
    private void AddItem()
    {
        StopFlash();
        editingItem = items.Count;
        BuildItems();
        itemBox?.Focus();
    }

    // An item's pencil, its line a box with the keyboard in it and all of it picked out to type over
    private void EditItem(int at)
    {
        if (at < 0 || at >= items.Count)
            return;

        StopFlash();
        editingItem = at;
        BuildItems();
        itemBox?.Focus();
        itemBox?.SelectAll();
    }

    // The line being typed put in, the same text on another line taken out. An empty line goes nowhere, the same as
    // leaving it
    private void CommitItem()
    {
        if (editingItem < 0 || itemBox is null)
            return;

        string item = itemBox.Text.Trim();
        if (item.Length == 0)
        {
            CancelItem();
            return;
        }

        StopFlash();
        var next = new List<string>(items);
        if (editingItem < next.Count)
            next[editingItem] = item;
        else
            next.Add(item);

        int kept = editingItem < items.Count ? editingItem : next.Count - 1;
        items = [.. next.Where((each, at) => at == kept || each != item)];
        editingItem = -1;
        BuildItems();
        FocusItemLine(items.IndexOf(item));
        page.Changed(Setting.Key, UIFormList.Value(items));
    }

    // The line being typed left as it was, the keyboard back on its pencil (or the add button for a new one)
    private void CancelItem()
    {
        if (editingItem < 0)
            return;

        StopFlash();
        int was = editingItem;
        editingItem = -1;
        BuildItems();
        FocusItemLine(was);
    }

    // An item's bin, the keyboard on the line that took its place, or the add button when it was the last
    private void RemoveItem(int at)
    {
        if (at < 0 || at >= items.Count)
            return;

        StopFlash();
        items = [.. items.Where((_, index) => index != at)];
        editingItem = -1;
        BuildItems();
        FocusItemLine(at);
        page.Changed(Setting.Key, UIFormList.Value(items));
    }

    // The keyboard on a line's pencil, the add button past the last line
    private void FocusItemLine(int at)
    {
        if (at >= 0 && at < itemEdits.Count)
            itemEdits[at].Focus();
        else
            addListItem?.Focus();
    }

    /// <summary>What a row of a list shows, the line being typed as it was before, empty for any other row.</summary>
    public IReadOnlyList<string> ItemsShown => items;

    /// <summary>The line of a list being typed, as many as there are for a new one, -1 for none.</summary>
    public int EditingItem => editingItem;

    /// <summary>The box of the line of a list being typed, null while none is.</summary>
    public TextBox? ItemBoxShown => itemBox;

    /// <summary>Where a list's add button is, and every line's pencil and bin, for a test to press with the pointer.</summary>
    public (UIRect Add, UIRect[] Edits, UIRect[] Removes) ItemBounds =>
        (addListItem?.Bounds ?? default, [.. itemEdits.Select(button => button.Bounds)], [.. itemRemoves.Select(button => button.Bounds)]);
}
