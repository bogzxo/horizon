namespace Horizon.UI.Components;

// Going straight to one setting, a link to it from somewhere else (VS Code's "open settings" at a key). The search
// emptied, its section scrolled to, the row brought into sight and flashed in the accent, and the keyboard on what sets it
public partial class SettingsPage
{
    // The setting being gone to, and how many updates it's waited for the page to be laid out around it since
    private string? revealing;
    private int revealSteps;
    private bool arrived;

    /// <summary>
    /// Goes to one setting by its key. The search is emptied, the page scrolls to its section and then just far enough
    /// for its row to show, the row flashes in the accent and fades back, and what sets it gets the keyboard. Asked for
    /// before the page is built (as it opens) it's done once it is. False for a key with no setting.
    /// </summary>
    public bool Reveal(string key)
    {
        if (!settings.Any(setting => setting.Key == key))
            return false;

        if (search.Text.Length > 0)
            Find(string.Empty);

        scrollBack = null;
        revealing = key;
        revealSteps = 0;
        arrived = false;

        // Flashed and given the keyboard there and then when the row's there already, the scrolling waits for the layout
        if (!dirty && rowOf.TryGetValue(key, out SettingRow? row))
            Arrive(row);
        return true;
    }

    /// <summary>The key of the row that's flashing from being gone to, null once it's faded. For a test.</summary>
    public string? FlashingKey => rowOf.Values.FirstOrDefault(row => row.Flashing)?.Setting.Key;

    /// <summary>Whether a setting's row is one the search left. For a test.</summary>
    public bool IsRowShown(string key) => rowOf.TryGetValue(key, out SettingRow? row) && row.Visible;

    /// <summary>The key of the row whatever has the keyboard is in, null when it's in none of them. For a test.</summary>
    public string? KeyWithKeyboard => FocusedKey;

    /// <summary>Whether a setting's row is showing and the whole of it is inside what the rows are scrolled to. For a test.</summary>
    public bool IsRowInView(string key)
    {
        if (!rowOf.TryGetValue(key, out SettingRow? row) || !row.Visible || row.Bounds.IsEmpty)
            return false;

        UIRect view = scroll.Bounds.Shrink(scroll.Padding);
        return row.Bounds.Min.Y >= view.Min.Y - 1.0f && row.Bounds.Max.Y <= view.Max.Y + 1.0f;
    }

    /// <summary>What a setting's name says on its row, markup and all, null for a key with no row. For a test to see what a search lit.</summary>
    public string? TitleShown(string key) => rowOf.TryGetValue(key, out SettingRow? row) ? row.TitleText : null;

    /// <summary>What a setting's sentence says on its row, markup and all, null for a key with no row.</summary>
    public string? HelpShown(string key) => rowOf.TryGetValue(key, out SettingRow? row) ? row.HelpText : null;

    /// <summary>
    /// Where the parts of a row are, the row, its name, the key after it, its sentence and what sets it (the first thing
    /// of it the keyboard goes to). Empty for a part it hasn't got, null for a key with no row. For a test of the columns.
    /// </summary>
    public (UIRect Row, UIRect Title, UIRect Key, UIRect Help, UIRect Control)? RowParts(string key) =>
        rowOf.TryGetValue(key, out SettingRow? row) ? (row.Bounds, row.TitleBounds, row.KeyBounds, row.HelpBounds, row.FirstControl()?.Bounds ?? default) : null;

    /// <summary>Whether a row is in a little for hanging off a switch and whether it's faint for that switch being off, null for a key with no row. For a test.</summary>
    public (bool Indented, bool Dimmed)? DependentShown(string key) =>
        rowOf.TryGetValue(key, out SettingRow? row) ? (row.Indented, row.Dimmed) : null;

    /// <summary>What a row's reset says when the pointer rests on it, null for a key with no row. For a test.</summary>
    public string? ResetTooltipOf(string key) => rowOf.TryGetValue(key, out SettingRow? row) ? row.ResetTooltip : null;

    /// <summary>Whether a row draws its accent line down its edge, null for a key with no row. For a test.</summary>
    public bool? RowMarked(string key) => rowOf.TryGetValue(key, out SettingRow? row) ? row.Modified : null;

    /// <summary>What a row's note after its key says (<see cref="NoteOf"/>), null for none or no row. For a test.</summary>
    public string? NoteShown(string key) => rowOf.TryGetValue(key, out SettingRow? row) ? row.NoteText : null;

    /// <summary>What a row's pill says a search found it by, plain, null for none or no row. For a test.</summary>
    public string? FoundByShown(string key) => rowOf.TryGetValue(key, out SettingRow? row) && row.Visible ? row.FoundByText : null;

    // The row flashed and the keyboard put on what sets it. What had the keyboard is taken as seen already, or the
    // scroll to a focused row would go by where the row was before the search was emptied
    private void Arrive(SettingRow row)
    {
        arrived = true;
        row.Flash();
        if (row.FirstControl() is { } control)
        {
            control.Focus();
            focusSeen = Module?.Compositor.Focus;
        }
    }

    // Once a row's been laid out with the search emptied its section goes to the top, and once that's been laid out the
    // row is scrolled into sight (a row far down a long section is below the bottom with its heading at the top)
    private void UpdateReveal()
    {
        if (revealing is not { } key || dirty)
            return;

        if (!rowOf.TryGetValue(key, out SettingRow? row))
        {
            revealing = null;
            return;
        }

        // Built since it was asked for, flashed now
        if (!arrived)
            Arrive(row);

        if (row.Bounds.IsEmpty)
            return;

        revealSteps++;
        if (revealSteps == 2)
            GoTo(row.Setting.Group);
        else if (revealSteps >= 3)
        {
            scroll.ScrollIntoView(row);
            revealing = null;
        }
    }
}
