using Horizon.Engine;

using Silk.NET.Input;

namespace Horizon.UI.Components;

// The page with the keyboard and nothing else, the way VS Code's settings go. Down out of the search into the rows, Up
// and Down along them, Alt with them (or Ctrl with the pages) a section at a time, Home and End to the ends, Ctrl+Left
// over to the sections down the left and Ctrl+F back to the search, or / from anything that isn't typed into
public partial class SettingsPage
{
    // Updates left to give the search the keyboard after a Ctrl+F, and whether the page had it the update before
    private int findFor;
    private bool hadKeyboard;

    // Ctrl+F anywhere in the page gives the search the keyboard with all of it picked out to type over. A program's own
    // Ctrl+F (Dawn's find bar) can take the keyboard after the page did, so the search gets it again the next update
    private void UpdateKeys()
    {
        bool has = HasKeyboard();
        if (GameEngine.Instance?.Input.Keyboard is { } keyboard && keyboard.Control && keyboard.WasPressed(Key.F) && (has || hadKeyboard))
            findFor = 2;

        if (findFor > 0)
        {
            findFor--;
            FocusSearch();
            has = true;
        }

        hadKeyboard = has;
    }

    // Whether control is held, Ctrl+PageDown comes as a page down like any other
    private static bool ControlHeld => GameEngine.Instance?.Input.Keyboard is { Control: true };

    // Down in the search goes to the first row it left, the way a list under a box is walked into, and a section at a
    // time from there with Alt or Ctrl
    private void SearchKey(char key)
    {
        switch (key)
        {
            case UIKeyboard.DOWN:
                FocusFirstRow();
                break;
            case UIKeyboard.MOVE_LINES_DOWN:
                MoveSection(1);
                break;
            case UIKeyboard.PAGE_DOWN when ControlHeld:
                MoveSection(1);
                break;
        }
    }

    /// <summary>
    /// A key that came to what sets a row before the control did anything with it, true when the page took it. Up and Down
    /// go row to row (a dropdown that's open keeps them), Alt with them and Ctrl with the pages go a section at a time, and
    /// for what isn't typed into Home and End go to the first and the last row, Ctrl+Left over to the sections and / to
    /// the search.
    /// </summary>
    internal bool RowKey(SettingRow row, UIComponent control, char key)
    {
        bool typing = control is TextBox;
        switch (key)
        {
            case UIKeyboard.UP or UIKeyboard.DOWN:
                if (control is Dropdown { IsOpen: true })
                    return false;
                MoveRow(row, key == UIKeyboard.DOWN ? 1 : -1);
                return true;
            case UIKeyboard.MOVE_LINES_UP or UIKeyboard.MOVE_LINES_DOWN:
                MoveSection(key == UIKeyboard.MOVE_LINES_DOWN ? 1 : -1);
                return true;
            case UIKeyboard.PAGE_UP or UIKeyboard.PAGE_DOWN when ControlHeld:
                MoveSection(key == UIKeyboard.PAGE_DOWN ? 1 : -1);
                return true;
            case UIKeyboard.HOME or UIKeyboard.END when !typing:
                var shown = RowsWithControls();
                if (shown.Count > 0)
                    FocusRow(key == UIKeyboard.HOME ? shown[0] : shown[^1]);
                return true;
            case UIKeyboard.WORD_LEFT when !typing:
                return FocusNav();
            case '/' when !typing:
                FocusSearch();
                return true;
        }

        return false;
    }

    // The rows showing that have something to set them, in the order they're down the page
    private List<SettingRow> RowsWithControls()
    {
        var shown = new List<SettingRow>();
        foreach (UIComponent child in rows.Children)
        {
            if (child is SettingRow { Visible: true } row && row.FirstControl() is not null)
                shown.Add(row);
        }

        return shown;
    }

    // The row whatever has the keyboard is in, null for none
    private SettingRow? FocusedRow()
    {
        for (UIComponent? at = Module?.Compositor.Focus; at is not null; at = at.Parent)
        {
            if (at is SettingRow row)
                return row;
        }

        return null;
    }

    // The keyboard on what sets a row, the row scrolled into sight the next update
    private static void FocusRow(SettingRow row) => row.FirstControl()?.Focus();

    /// <summary>The key of the row whatever has the keyboard is in, null when it's in none of them. For a test.</summary>
    public string? FocusedKey => FocusedRow()?.Setting.Key;

    /// <summary>
    /// The keyboard moved a row down (or up with a negative) from the row that has it, the way Down and Up do. From
    /// outside the rows it goes to the first one, and up from the first it goes back to the search. False when there was
    /// nowhere to go.
    /// </summary>
    public bool MoveRow(int delta) => MoveRow(FocusedRow(), delta);

    private bool MoveRow(SettingRow? from, int delta)
    {
        var shown = RowsWithControls();
        int at = from is null ? -1 : shown.IndexOf(from);
        if (at < 0)
            return delta > 0 && FocusFirstRow();

        int to = at + delta;
        if (to < 0)
        {
            FocusSearch();
            return true;
        }

        if (to >= shown.Count)
            return false;

        FocusRow(shown[to]);
        return true;
    }

    /// <summary>
    /// The page gone to the next section showing (or the one before with a negative) from the one the keyboard is in,
    /// the one at the top when it's in none, and the keyboard put on its first row, Alt+Down and Ctrl+PageDown. Sections a
    /// search left nothing in are passed over. False when there's no section that way.
    /// </summary>
    public bool MoveSection(int delta)
    {
        var groups = SectionsInOrder().Where(pair => pair.Header.Visible).Select(pair => pair.Group).ToList();
        if (groups.Count == 0 || delta == 0)
            return false;

        string? from = FocusedRow()?.Setting.Group ?? CurrentGroup;
        int at = from is null ? -1 : groups.IndexOf(from);
        int to = at < 0 ? (delta > 0 ? 0 : groups.Count - 1) : at + Math.Sign(delta);
        if (to < 0 || to >= groups.Count)
            return false;

        EnterSection(groups[to]);
        return true;
    }

    // A section scrolled to the top and the keyboard on its first row, or on the first thing of its panel when it's a
    // panel and no rows. What has the keyboard is taken as seen, or the row would be scrolled to on top of the heading
    private void EnterSection(string group)
    {
        GoTo(group);
        CurrentGroup = group;
        UIComponent? first = null;
        foreach (UIComponent child in rows.Children)
        {
            if (child is SettingRow { Visible: true } row && row.Setting.Group == group && row.FirstControl() is { } control)
            {
                first = control;
                break;
            }
        }

        if (first is null && panels.Find(panel => panel.Group == group).Content is { Visible: true } content)
        {
            var found = new List<UIComponent>();
            UIModule.CollectFocusable(content, found);
            first = found.FirstOrDefault();
        }

        if (first is null)
            return;

        first.Focus();
        focusSeen = Module?.Compositor.Focus;
    }

    /// <summary>The keyboard given to what sets the first row showing, as Down in the search does. False when no row is showing.</summary>
    public bool FocusFirstRow()
    {
        var shown = RowsWithControls();
        if (shown.Count == 0)
            return false;

        FocusRow(shown[0]);
        return true;
    }

    /// <summary>The keyboard given to the search with all of it picked out to type over, what Ctrl+F in the page does.</summary>
    public void FocusSearch()
    {
        search.Focus();
        search.SelectAll();
    }

    /// <summary>
    /// The keyboard given to the sections down the left, on the one lit (or the first showing), what Ctrl+Left from a row
    /// does. False when none is showing.
    /// </summary>
    public bool FocusNav()
    {
        if (commonLink is { Visible: true } && CommonIsStop)
        {
            commonLink.Focus();
            return true;
        }

        var links = VisibleLinks().ToList();
        if (links.Count == 0)
            return shownParts.FirstOrDefault(part => part.Heading.Visible).Part is { } part && FocusPart(part);

        (links.Find(link => link.Group == CurrentGroup) ?? links[0]).Focus();
        return true;
    }

    /// <summary>The section whose link down the left has the keyboard, null when none has. For a test.</summary>
    public string? FocusedSection => Module?.Compositor.Focus is SectionLink link && link.Parent == nav ? link.Group : null;

    /// <summary>
    /// A key (<see cref="UIKeyboard"/>'s codes) given to whatever in the page has the keyboard as if it was typed, through
    /// the control and on to the page the way a real one goes. For a test, false when nothing in the page has the keyboard.
    /// </summary>
    public bool Press(char key)
    {
        if (!HasKeyboard() || Module?.Compositor.Focus is not { } focus)
            return false;

        focus.OnTextInput(key);
        return true;
    }

    /// <summary>Whether whatever has the keyboard is somewhere in the page, so a program can keep its own keys off it.</summary>
    public bool HasKeyboard()
    {
        for (UIComponent? at = Module?.Compositor.Focus; at is not null; at = at.Parent)
        {
            if (at == this)
                return true;
        }

        return false;
    }
}
