using Horizon.Input;
using Horizon.UI.Components;

using Key = Silk.NET.Input.Key;

namespace Horizon.UI;

// Where the keyboard goes as windows come and go and what a press hands it to, the menus that take the keys while
// they're open, and whether the ring round what has the keyboard shows.
public partial class UICompositor
{
    /// <summary>
    /// Whether the keyboard was the last thing to move about the UI, which is when the ring round what has it is drawn.
    /// A click on a button or a list rings nothing, a key after it does, the web's focus-visible. Clicking the explorer's
    /// icon drew a ring round the first row of the tree, and a click in a box drew two.
    /// </summary>
    public bool FocusVisible { get; private set; }

    /// <summary>
    /// Whether a menu that is open takes the keys right now (see <see cref="UIComponent.TakesPopupKeys"/>), for a program
    /// whose own shortcuts keep out of the way meanwhile.
    /// </summary>
    public bool KeysToPopup => KeysPopup(modules) is not null;

    // The menu on top that takes the keys, the last module's first, null for none
    private static UIComponent? KeysPopup(UIModule[] snapshot)
    {
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            if (snapshot[i].Enabled && snapshot[i].Popup is { TakesPopupKeys: true } popup)
                return popup;
        }

        return null;
    }

    // Every window the keyboard comes into from outside of it notes who had it, to give it back as the window goes. From
    // nothing it keeps what it had, a click on the title bar on the way in isn't who to go back to
    private static void NoteWindowsEntered(UIComponent? from, UIComponent? to)
    {
        if (from is null)
            return;

        for (UIComponent? at = to; at is not null; at = at.Parent)
        {
            if (at is WindowPanel window && !window.Holds(from))
                window.KeyboardBefore = from;
        }
    }

    /// <summary>
    /// Who gets the keyboard back as a window goes, whoever had it before it came into the window. One that's gone as
    /// well hands on to whoever had it before the window it was in, nothing at the end of that.
    /// </summary>
    internal static UIComponent? KeyboardAfter(WindowPanel window)
    {
        WindowPanel leaving = window;
        UIComponent? before = window.KeyboardBefore;
        for (int depth = 0; depth < 8 && before is not null; depth++)
        {
            if (before.IsShownInHierarchy && !leaving.Holds(before))
                return before;

            if (WindowOf(before) is not { } around)
                return null;

            leaving = around;
            before = around.KeyboardBefore;
        }

        return null;
    }

    // The window a component is in, or is, null for one in none
    private static WindowPanel? WindowOf(UIComponent component)
    {
        for (UIComponent? at = component; at is not null; at = at.Parent)
        {
            if (at is WindowPanel window)
                return window;
        }

        return null;
    }

    /// <summary>
    /// Helper method for the keyboard that's on something that left the UI or was hidden with the window it's in. A
    /// window put away (Escape, its x, the code taking it down) gives the keyboard back to whoever had it before, a
    /// piece of a window that's still up leaves it with the window, so Escape still closes it. Anything else left the
    /// UI and takes the keyboard with it, as ever.
    /// </summary>
    private void KeepKeyboardSomewhere()
    {
        if (focus is not { } lost)
            return;

        bool left = lost.Module is not { Enabled: true };
        WindowPanel? window = WindowOf(lost);
        if (window is null)
        {
            if (left)
                Focus = null;
            return;
        }

        if (window.IsShownInHierarchy)
        {
            if (left)
                Focus = window;
            return;
        }

        Focus = KeyboardAfter(window);
    }

    /// <summary>
    /// What a press on a component gives the keyboard to. One that takes it, or failing that the window it's in, so a
    /// click on a window's title bar or a button in it leaves Escape closing it. Anywhere else the keyboard is let go of.
    /// </summary>
    private static UIComponent? FocusForPress(UIComponent over)
    {
        if (over.Focusable && over.EnabledInHierarchy)
            return over;

        return WindowOf(over) is { } window && window.IsShownInHierarchy ? window : null;
    }

    // The keys that go about the UI rather than type, any of them pressed and the ring shows
    private static readonly Key[] Moving =
    [
        Key.Tab, Key.Up, Key.Down, Key.Left, Key.Right, Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.F6, Key.Enter, Key.Space
    ];

    // Whether the keys moved about this update, a key that goes about or a shortcut
    private static bool KeysMoved(Keyboard keyboard)
    {
        if (!keyboard.AnyPressed)
            return false;

        if (keyboard.Primary || keyboard.Alt)
            return true;

        foreach (Key key in Moving)
        {
            if (keyboard.WasPressed(key))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Helper method to give the keys of this update to a menu that is open and takes them, and to nothing else. What was
    /// typed meanwhile is thrown away, the code under a menu got the arrows that went down it.
    /// </summary>
    private bool RouteToPopup(UIModule[] snapshot, Keyboard keyboard)
    {
        if (KeysPopup(snapshot) is not { } popup)
            return false;

        popup.OnPopupKeys(keyboard);
        UIKeyboard.Clear();
        while (UIKeyboard.TryReadKey(out _))
        {
        }

        return true;
    }
}
