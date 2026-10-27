using System.Numerics;

using Horizon.UI.Skinning;

namespace Horizon.UI.Components;

/// <summary>A key a menu that's open goes by, the arrows, Home and End, Enter (or Space), Escape and a letter.</summary>
internal enum MenuKey { Up, Down, Left, Right, Home, End, Enter, Escape, Letter }

/// <summary>What a key did to the lists of a menu, and what's left for whoever opened it to do.</summary>
internal enum MenuKeyResult
{
    // Nothing came of it
    None,

    // The lists took it, a highlight moved or a menu opened or shut off an item
    Handled,

    // An item was picked, the menu goes and the item does what it does
    Picked,

    // Escape on the first list, the menu goes
    Close,

    // Left or Right on the first list with nothing to open or shut, the bar goes to the menu next to this one
    Previous,
    Next,

    // Left or Escape on a list open off another, which that one shuts (only ever seen between two lists)
    Back
}

// The keys going down the lists of a menu, VS Code's and every desktop's. Up and Down move a highlight along the items
// that can be picked, round past the ends, Right (or Enter) opens the menu of the item it's on and Left or Escape shuts
// it again, Enter picks, a letter goes to the next item starting with it. The pointer has the say again once it moves.
internal sealed partial class MenuPanel
{
    // The item the keys are on, -1 while the pointer has the say
    private int keyed = -1;

    // Where the pointer was the last time the lists were tracked, and where it was as the keys took over
    private Vector2 tracked, keyedPointer;

    /// <summary>The item the keys are on in the deepest list they're in, null while the pointer has the say.</summary>
    public MenuItem? KeyedItem => child is { keyed: >= 0 } inner ? inner.KeyedItem : keyed >= 0 && keyed < menu.Items.Count ? menu.Items[keyed] : null;

    /// <summary>Puts the keys on the first item that can be picked, for a menu opened with them, the pointer where it is now.</summary>
    public void KeyFirst(UISkin skin, Vector2 pointer)
    {
        Point(pointer);
        KeyTo(skin, Step(-1, 1));
    }

    // Whether the keys are on an item and the pointer hasn't moved since they went there
    private bool KeysHave => keyed >= 0 && Vector2.DistanceSquared(tracked, keyedPointer) < 4.0f;

    private void KeyTo(UISkin skin, int index)
    {
        keyed = index;
        keyedPointer = tracked;
        if (index >= 0)
            Reveal(skin, index);
    }

    // Whether the keys can stop on an item, one that's there, isn't a rule and can be picked or opened
    private bool Stops(int index) => ShownAt(index) && menu.Items[index] is { IsSeparator: false } item && item.Enabled;

    // The next item the keys stop on from one, a step along, round past the ends, -1 for none at all
    private int Step(int from, int by)
    {
        int count = menu.Items.Count;
        if (count == 0)
            return -1;

        int at = from < 0 ? (by > 0 ? -1 : count) : from;
        for (int i = 0; i < count; i++)
        {
            at = ((at + by) % count + count) % count;
            if (Stops(at))
                return at;
        }

        return -1;
    }

    /// <summary>
    /// A key pressed with the menu open, handed to the deepest list the keys are in. The item picked comes back through
    /// <paramref name="picked"/> when it was picked.
    /// </summary>
    public MenuKeyResult Key(UISkin skin, UIRect frame, Vector2 pointer, MenuKey key, char letter, out MenuItem? picked)
    {
        // A key pressed gives the keys the say again, wherever the pointer went meanwhile
        Point(pointer);
        return Key(skin, frame, key, letter, true, out picked);
    }

    // Helper method to note where the pointer is, in this list and the ones open off it
    private void Point(Vector2 pointer)
    {
        tracked = pointer;
        child?.Point(pointer);
    }

    private MenuKeyResult Key(UISkin skin, UIRect frame, MenuKey key, char letter, bool first, out MenuItem? picked)
    {
        picked = null;

        // A list open off this one that the keys went into has them
        if (child is { keyed: >= 0 } inner)
        {
            MenuKeyResult inside = inner.Key(skin, frame, key, letter, false, out picked);
            if (inside != MenuKeyResult.Back)
                return inside;

            int was = childIndex;
            CloseChild();
            KeyTo(skin, was);
            return MenuKeyResult.Handled;
        }

        // The pointer's item is where the keys start from, or the item whose list is open
        int from = KeysHave ? keyed : childIndex;
        switch (key)
        {
            case MenuKey.Down or MenuKey.Up:
                CloseChild();
                KeyTo(skin, Step(from, key == MenuKey.Down ? 1 : -1));
                return MenuKeyResult.Handled;

            case MenuKey.Home or MenuKey.End:
                CloseChild();
                KeyTo(skin, key == MenuKey.Home ? Step(-1, 1) : Step(-1, -1));
                return MenuKeyResult.Handled;

            case MenuKey.Letter:
                int found = -1;
                for (int step = 1; step <= menu.Items.Count && found < 0; step++)
                {
                    int at = ((from < 0 ? -1 : from) + step + menu.Items.Count) % menu.Items.Count;
                    if (Stops(at) && menu.Items[at].Label.TrimStart('&').StartsWith(letter.ToString(), StringComparison.OrdinalIgnoreCase))
                        found = at;
                }

                if (found < 0)
                    return MenuKeyResult.None;

                CloseChild();
                KeyTo(skin, found);
                return MenuKeyResult.Handled;

            case MenuKey.Right or MenuKey.Enter when from >= 0 && menu.Items[from] is { Submenu: { } submenu } opening && opening.Enabled:
                Reveal(skin, from);
                Open(skin, frame, from, submenu, VisibleBounds(skin, from));
                keyed = from;
                keyedPointer = tracked;
                child!.KeyFirst(skin, tracked);
                return MenuKeyResult.Handled;

            case MenuKey.Enter when from >= 0 && Stops(from):
                picked = menu.Items[from];
                return MenuKeyResult.Picked;

            case MenuKey.Enter:
                return MenuKeyResult.Handled;

            case MenuKey.Right:
                return first ? MenuKeyResult.Next : MenuKeyResult.Handled;

            case MenuKey.Left:
                return first ? MenuKeyResult.Previous : MenuKeyResult.Back;

            case MenuKey.Escape:
                return first ? MenuKeyResult.Close : MenuKeyResult.Back;
        }

        return MenuKeyResult.None;
    }

    /// <summary>
    /// Helper method to turn the keys pressed this update into what a menu goes by, one of them, null for none. Shared by
    /// the bar and the context menu.
    /// </summary>
    public static (MenuKey Key, char Letter)? Read(Horizon.Input.Keyboard keyboard)
    {
        if (keyboard.WasPressed(Silk.NET.Input.Key.Escape)) return (MenuKey.Escape, '\0');
        if (keyboard.WasPressed(Silk.NET.Input.Key.Down)) return (MenuKey.Down, '\0');
        if (keyboard.WasPressed(Silk.NET.Input.Key.Up)) return (MenuKey.Up, '\0');
        if (keyboard.WasPressed(Silk.NET.Input.Key.Left)) return (MenuKey.Left, '\0');
        if (keyboard.WasPressed(Silk.NET.Input.Key.Right)) return (MenuKey.Right, '\0');
        if (keyboard.WasPressed(Silk.NET.Input.Key.Home) || keyboard.WasPressed(Silk.NET.Input.Key.PageUp)) return (MenuKey.Home, '\0');
        if (keyboard.WasPressed(Silk.NET.Input.Key.End) || keyboard.WasPressed(Silk.NET.Input.Key.PageDown)) return (MenuKey.End, '\0');
        if (keyboard.WasPressed(Silk.NET.Input.Key.Enter) || keyboard.WasPressed(Silk.NET.Input.Key.KeypadEnter) || keyboard.WasPressed(Silk.NET.Input.Key.Space))
            return (MenuKey.Enter, '\0');

        // A letter with nothing held, the item starting with it. Alt and a letter is the bar's, another menu
        if (keyboard.Control || keyboard.Primary || keyboard.Alt)
            return null;
        for (var key = Silk.NET.Input.Key.A; key <= Silk.NET.Input.Key.Z; key++)
        {
            if (keyboard.WasPressed(key))
                return (MenuKey.Letter, (char)('a' + (key - Silk.NET.Input.Key.A)));
        }

        return null;
    }
}
