using System.Numerics;
using System.Text;

using Horizon.Rendering;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

// Typing into a tree that has the keyboard goes to the next row whose name has what was typed, VS Code's type to find.
// A small box at the tree's top right says what's typed, the part of each row showing that matches is lit, Backspace
// takes a letter off and Escape (or the keyboard going somewhere else) lets it go. The same letter typed again goes on to
// the next row starting with it, the way a list of files on the desktop does. Only the rows that are showing are looked
// through, nothing is made for a letter but the one string the box shows.
public partial class TreeView
{
    /// <summary>Whether typing a letter while the tree has the keyboard finds a row by its name.</summary>
    public bool TypeToFind { get; set; }

    /// <summary>What the find box says after what's typed when no row showing has it.</summary>
    public string FindNothingText { get; set; } = "No match";

    // What's typed, and the same as a string for the box, made again only when a letter comes or goes
    private readonly StringBuilder finding = new();
    private string findShown = string.Empty;
    private bool findMissed;

    // How far in from the tree's edges the box sits, and its padding
    private const float FIND_INSET = 6.0f, FIND_PAD = 8.0f;

    /// <summary>What's typed to find a row, empty while nothing is.</summary>
    public string FindText => findShown;

    /// <summary>Whether what's typed is in no row showing.</summary>
    public bool FindMissed => findMissed && findShown.Length > 0;

    /// <summary>Forgets what was typed to find a row, the box goes.</summary>
    public void ClearFind()
    {
        if (finding.Length == 0)
            return;

        finding.Clear();
        findShown = string.Empty;
        findMissed = false;
    }

    // A letter typed for finding, true when it was one
    private bool FindKey(char character)
    {
        if (!TypeToFind)
            return false;

        if (character == UIKeyboard.BACKSPACE && finding.Length > 0)
        {
            finding.Length--;
            findShown = finding.ToString();
            if (finding.Length > 0)
                FindFrom(IndexOf(selected), stay: true);
            else
                findMissed = false;
            return true;
        }

        // A space only once something's typed, alone it presses the row
        if (char.IsControl(character) || character is >= '' and <= '' || (character == ' ' && finding.Length == 0))
            return false;

        // The same letter again goes on to the next row that starts with it, rather than looking for "aa"
        bool again = finding.Length == 1 && char.ToLowerInvariant(finding[0]) == char.ToLowerInvariant(character);
        if (!again)
        {
            finding.Append(character);
            findShown = finding.ToString();
        }

        FindFrom(IndexOf(selected), stay: !again);
        return true;
    }

    // The next row from one with what's typed, starting at it (stay) or after it, a row starting with it before one that
    // only has it somewhere. Selected and shown, the owner told the way the arrows tell it
    private void FindFrom(int from, bool stay)
    {
        if (rows.Count == 0 || finding.Length == 0)
            return;

        ReadOnlySpan<char> typed = findShown;
        int start = Math.Max(0, from) + (stay || from < 0 ? 0 : 1);
        int found = -1;
        for (int pass = 0; pass < 2 && found < 0; pass++)
        {
            for (int step = 0; step < rows.Count; step++)
            {
                int at = (start + step) % rows.Count;
                ReadOnlySpan<char> text = rows[at].Item.Text;
                bool hit = pass == 0 ? text.StartsWith(typed, StringComparison.OrdinalIgnoreCase) : text.Contains(typed, StringComparison.OrdinalIgnoreCase);
                if (hit)
                {
                    found = at;
                    break;
                }
            }
        }

        findMissed = found < 0;
        if (found < 0)
            return;

        alsoSelected.Clear();
        PressedByKey = true;
        try
        {
            Select(rows[found].Item, notify: ArrowsPress);
        }
        finally
        {
            PressedByKey = false;
        }
    }

    // Lets go of what's typed once neither the tree nor anything in it has the keyboard
    private void UpdateFind()
    {
        if (finding.Length > 0 && !IsFocused && !(editBox?.IsFocused ?? false))
            ClearFind();
    }

    // The part of a row's text that has what's typed, lit behind its letters. Always painted, see-through on a row
    // without it, so the row's quads stay the same ones as letters come and go
    private void PaintFound(UIDrawList list, UISkin skin, string rowText, float x, UIRect row, float scale, float textHeight)
    {
        int at = findShown.Length > 0 ? rowText.AsSpan().IndexOf(findShown, StringComparison.OrdinalIgnoreCase) : -1;
        if (at < 0)
        {
            list.RoundRect(new UIRect(new Vector2(x, row.Center.Y), new Vector2(x, row.Center.Y)), 3.0f, skin.Palette.Selection with { W = 0.0f });
            return;
        }

        float from = x + skin.Font.Measure(rowText.AsSpan(0, at), scale, markup: false).X;
        float width = skin.Font.Measure(rowText.AsSpan(at, findShown.Length), scale, markup: false).X;
        var lit = new UIRect(new Vector2(from - 1.0f, row.Center.Y - textHeight * 0.5f - 1.0f), new Vector2(from + width + 1.0f, row.Center.Y + textHeight * 0.5f + 1.0f));
        list.RoundRect(lit, 3.0f, skin.AccentColor with { W = 0.35f });
        list.RoundOutline(lit, 3.0f, 1.0f, skin.AccentColor with { W = 0.8f });
    }

    /// <summary>Where the find box is, in the module's units, empty while nothing is typed.</summary>
    public UIRect FindBounds { get; private set; }

    // The box at the top right with what's typed, a magnifier in front and in the error colour with a word after it when
    // no row has it
    private void PaintFindBox(UIDrawList list, UISkin skin, UIRect area, float right, float scale)
    {
        if (findShown.Length == 0)
        {
            FindBounds = default;
            return;
        }

        float line = LineHeight(skin);
        float icon = MathF.Round(line * 0.55f);
        Vector2 typed = skin.Font.Measure(findShown, scale, markup: false);
        string nothing = FindMissed ? FindNothingText : string.Empty;
        float nothingWidth = nothing.Length > 0 ? skin.Font.Measure(nothing, scale, markup: false).X + FIND_PAD : 0.0f;
        float width = MathF.Min(FIND_PAD + icon + ICON_GAP + typed.X + nothingWidth + FIND_PAD, area.Width - FIND_INSET * 2.0f);
        float height = line - 2.0f;
        var box = new UIRect(new Vector2(right - FIND_INSET - width, area.Max.Y - FIND_INSET - height), new Vector2(right - FIND_INSET, area.Max.Y - FIND_INSET));
        FindBounds = box;

        Vector4 edge = FindMissed ? skin.ErrorColor : skin.HighlightColor with { W = 0.85f };
        list.RoundRect(box, skin.CornerRadius, skin.PanelColor with { W = 1.0f });
        list.RoundRect(box, skin.CornerRadius, skin.FieldColor);
        list.RoundOutline(box, skin.CornerRadius, 1.0f, edge);

        float x = box.Min.X + FIND_PAD;
        list.Symbol("search", new UIRect(new Vector2(x, box.Center.Y - icon * 0.5f), new Vector2(x + icon, box.Center.Y + icon * 0.5f)), skin.TextColor with { W = 0.6f });
        x += icon + ICON_GAP;
        list.PushClip(new UIRect(new Vector2(x, box.Min.Y), new Vector2(box.Max.X - FIND_PAD * 0.5f, box.Max.Y)));
        list.Text(findShown, new UIRect(new Vector2(x, box.Min.Y), new Vector2(x + typed.X, box.Max.Y)), Origin.Left, scale, FindMissed ? skin.ErrorColor : skin.TextColor, markup: false);
        if (nothing.Length > 0)
        {
            float at = x + typed.X + FIND_PAD;
            list.Text(nothing, new UIRect(new Vector2(at, box.Min.Y), new Vector2(box.Max.X, box.Max.Y)), Origin.Left, scale, skin.TextColor with { W = 0.55f }, markup: false);
        }
        list.PopClip();
    }
}
