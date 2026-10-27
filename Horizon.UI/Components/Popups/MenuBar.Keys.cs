using System.Numerics;

using Horizon.Engine;
using Horizon.Input;
using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

using Key = Silk.NET.Input.Key;

namespace Horizon.UI.Components;

// The bar with the keys, the way Windows' and VS Code's go. Alt and a letter opens the menu that letter is the mnemonic
// of (the letter after an & in its title, or its first), Alt alone lights the first word and the letters, Left and Right
// go along the bar, Up and Down along a menu, Enter picks, Right opens the menu off an item and Left shuts it, Escape
// shuts a level. Nothing the bar does takes the keyboard from where it was, so it's there again once the menu shuts.
public partial class MenuBar
{
    // Whether the bar has the keys with no menu open, Alt pressed alone, and which word is lit then
    private bool armed;
    private int hot;

    // Alt held down, and whether anything else happened while it was (a key, a click), which makes it no tap
    private bool altDown, altSpoiled;

    // Where the pointer was when the keys last did something, it has the say again once it moves away
    private Vector2 keysPointer = new(float.NaN);

    /// <summary>
    /// Asked of Alt and a letter before the bar opens a menu with it, true when something else has that key (a command
    /// put on Alt+F) and the bar leaves it be. Null for nobody.
    /// </summary>
    public Func<Key, bool, bool>? AltKeyTaken { get; set; }

    /// <summary>Whether the bar has the keys with no menu open yet, after Alt alone.</summary>
    public bool Armed => armed && IsPopupOpen;

    /// <summary>The item of the open menu (or the deepest menu open off it) the keys are on, null while the pointer has the say.</summary>
    public MenuItem? KeyedItem => OpenMenu is not null ? Panel?.KeyedItem : null;

    // Pressing on a word of the bar or an item of a menu leaves the keyboard where it was, the code a menu came over
    // still has it once the menu's gone
    protected internal override bool LeavesFocus => true;

    // Open, or lit with Alt, the bar has the keys
    protected internal override bool TakesPopupKeys => IsPopupOpen;

    private bool PointerMovedSinceKeys => float.IsNaN(keysPointer.X) || Vector2.DistanceSquared(keysPointer, Pointer) > 4.0f;

    /// <summary>The title as it's shown, without the & that marks its mnemonic.</summary>
    internal static string TitleShown(string title)
    {
        int at = title.IndexOf('&');
        return at >= 0 && at + 1 < title.Length ? title.Remove(at, 1) : title;
    }

    // Where in the shown title the mnemonic letter is, the one after an &, or the first letter
    private static int MnemonicAt(string title)
    {
        int at = title.IndexOf('&');
        if (at >= 0 && at + 1 < title.Length)
            return at;

        string shown = TitleShown(title);
        for (int i = 0; i < shown.Length; i++)
        {
            if (char.IsLetterOrDigit(shown[i]))
                return i;
        }

        return -1;
    }

    /// <summary>The letter that opens a menu with Alt, lower case, nothing for a title without one.</summary>
    public static char MnemonicOf(Menu menu) => MnemonicAt(menu.Title) is >= 0 and var at ? char.ToLowerInvariant(TitleShown(menu.Title)[at]) : '\0';

    /// <summary>
    /// Opens a menu with the keys, its first item lit, the way Alt and its letter does. The keyboard stays where it was.
    /// </summary>
    public void OpenWithKeys(int index)
    {
        Open(index);
        if (OpenMenu is null || Skin is not { } skin || Panel is not { } shown)
            return;

        hot = index;
        shown.KeyFirst(skin, Pointer);
        keysPointer = Pointer;
    }

    /// <summary>Lights the first word of the bar for the keys with no menu open, what Alt alone does.</summary>
    public void Arm()
    {
        if (!EnabledInHierarchy || NextShown(-1, 1) is not (>= 0 and var first))
            return;

        Close();
        hot = first;
        armed = true;
        keysPointer = Pointer;
        OpenPopup();
    }

    // The next menu that's on the bar from one, a step along and round past the ends, -1 for none
    private int NextShown(int from, int by)
    {
        for (int i = 1; i <= menus.Count; i++)
        {
            int at = (((from < 0 && by < 0 ? menus.Count : from) + by * i) % menus.Count + menus.Count) % menus.Count;
            if (menus[at].Shown)
                return at;
        }

        return -1;
    }

    // The menu a letter opens, the next one with it after the one open when two share it
    private int MenuOf(char letter)
    {
        int from = OpenMenu is not null ? open : -1;
        for (int i = 1; i <= menus.Count; i++)
        {
            int at = ((from < 0 ? -1 : from) + i + menus.Count) % menus.Count;
            if (menus[at].Shown && MnemonicOf(menus[at]) == letter)
                return at;
        }

        return -1;
    }

    // Whether Alt can do anything with the bar right now, it's on screen and nothing's up over it, and the keyboard isn't
    // a terminal's, whose shell has Alt and its letters for itself
    private bool AltWorks() =>
        EnabledInHierarchy && IsShownInHierarchy && Module is { Dialog: null } module && module.Compositor.Focus is not { TakesRawKeys: true };

    /// <summary>Every update, Alt alone and Alt with a letter.</summary>
    private void UpdateAlt()
    {
        if (GameEngine.Instance?.Input.Keyboard is not { } keyboard)
            return;

        bool alt = keyboard.Alt;
        if (alt && !altDown)
        {
            altDown = true;
            altSpoiled = keyboard.Control || keyboard.Shift;
        }

        // Anything else while Alt is down makes it no tap, a key (Tab on the way to another program), a click
        if (altDown && (OtherKeyDown(keyboard) || Module?.Compositor.Pointer.Down == true))
            altSpoiled = true;

        if (alt)
            AltLetter(keyboard);

        if (!alt && altDown)
        {
            altDown = false;
            if (!altSpoiled && AltWorks())
            {
                if (IsPopupOpen) Close();
                else Arm();
            }
        }
    }

    // The keys held down, kept to look through every update Alt is
    private readonly List<Key> held = [];

    // Whether a key other than Alt is down, Alt and an arrow moving lines is no tap of Alt
    private bool OtherKeyDown(Keyboard keyboard)
    {
        keyboard.CopyDown(held);
        foreach (Key key in held)
        {
            if (key is not (Key.AltLeft or Key.AltRight))
                return true;
        }

        return false;
    }

    // Alt and a letter, the menu that letter is the mnemonic of, unless something else has the key
    private void AltLetter(Keyboard keyboard)
    {
        if (keyboard.Control || keyboard.Primary)
            return;

        for (Key key = Key.A; key <= Key.Z; key++)
        {
            if (!keyboard.WasPressed(key))
                continue;

            altSpoiled = true;
            if (!AltWorks() || AltKeyTaken?.Invoke(key, keyboard.Shift) == true)
                return;

            if (MenuOf((char)('a' + (key - Key.A))) is >= 0 and var index)
                OpenWithKeys(index);
            return;
        }
    }

    protected internal override void OnPopupKeys(Keyboard keyboard)
    {
        if (Skin is not { } skin || Module is not { } module || MenuPanel.Read(keyboard) is not { } read)
            return;

        var (key, letter) = read;

        keysPointer = Pointer;

        // Lit with Alt and nothing open, the keys go along the bar and open the word that's lit
        if (OpenMenu is null)
        {
            switch (key)
            {
                case MenuKey.Left or MenuKey.Right:
                    hot = Math.Max(0, NextShown(hot, key == MenuKey.Right ? 1 : -1));
                    break;
                case MenuKey.Down or MenuKey.Up or MenuKey.Enter:
                    OpenWithKeys(hot);
                    break;
                case MenuKey.Letter when MenuOf(letter) is >= 0 and var index:
                    OpenWithKeys(index);
                    break;
                case MenuKey.Escape:
                    Close();
                    break;
            }

            return;
        }

        if (Panel is not { } shown)
            return;

        switch (shown.Key(skin, module.Frame, Pointer, key, letter, out MenuItem? picked))
        {
            case MenuKeyResult.Picked:
                Close();
                picked?.OnPicked?.Invoke();
                break;
            case MenuKeyResult.Close:
                Close();
                break;
            case MenuKeyResult.Previous or MenuKeyResult.Next:
                OpenWithKeys(NextShown(open, key == MenuKey.Right ? 1 : -1));
                break;
        }
    }

    // The mnemonic underlined while the keys are on the bar or Alt is held, always painted and see-through otherwise, so
    // the quads of the word don't shift as it comes and goes
    private void PaintMnemonic(UIDrawList list, UISkin skin, Menu menu, UIRect title, float scale)
    {
        int at = MnemonicAt(menu.Title);
        if (at < 0)
            return;

        // Measured only while it shows, the pieces of the title measured were garbage every tick the bar was painted
        bool lit = Armed || OpenMenu is not null && !PointerMovedSinceKeys || altDown && !altSpoiled;
        if (!lit)
        {
            list.Rect(new UIRect(title.Center, title.Center), skin.TextColor with { W = 0.0f });
            return;
        }

        string shown = TitleShown(menu.Title);
        float width = skin.Font.Measure(shown, scale, markup: false).X;
        float left = title.Center.X - width * 0.5f + skin.Font.Measure(shown.Substring(0, at), scale, markup: false).X;
        float right = title.Center.X - width * 0.5f + skin.Font.Measure(shown.Substring(0, at + 1), scale, markup: false).X;
        float under = MathF.Round(title.Center.Y - skin.Font.LineHeight * scale * 0.42f);
        list.Rect(new UIRect(new Vector2(left, under - 1.0f), new Vector2(right, under)), skin.TextColor with { W = skin.TextColor.W * 0.8f });
    }
}
