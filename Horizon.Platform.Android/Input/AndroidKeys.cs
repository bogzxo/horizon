using Android.Views;

using Horizon.Input;

namespace Horizon.Platform.Android;

/// <summary>
/// Android's key codes as the engine's keys, for a hardware keyboard (a Bluetooth one, a tablet's cover keyboard). The
/// numbers are KeyEvent's, which never change between releases, so ranges of them are mapped by arithmetic. A key the
/// engine has no name for is null and goes no further.
/// </summary>
internal static class AndroidKeys
{
    public static Key? Of(Keycode code)
    {
        int value = (int)code;

        // KEYCODE_A 29 to KEYCODE_Z 54, KEYCODE_0 7 to KEYCODE_9 16, F1 131 to F12 142, NUMPAD_0 144 to NUMPAD_9 153
        if (value is >= 29 and <= 54) return (Key)((int)Key.A + value - 29);
        if (value is >= 7 and <= 16) return (Key)((int)Key.Number0 + value - 7);
        if (value is >= 131 and <= 142) return (Key)((int)Key.F1 + value - 131);
        if (value is >= 144 and <= 153) return (Key)((int)Key.Keypad0 + value - 144);

        return code switch
        {
            Keycode.Space => Key.Space,
            Keycode.Enter => Key.Enter,
            Keycode.Tab => Key.Tab,
            Keycode.Del => Key.Backspace,
            Keycode.ForwardDel => Key.Delete,
            Keycode.Escape => Key.Escape,
            Keycode.Insert => Key.Insert,
            Keycode.MoveHome => Key.Home,
            Keycode.MoveEnd => Key.End,
            Keycode.PageUp => Key.PageUp,
            Keycode.PageDown => Key.PageDown,
            Keycode.DpadUp => Key.Up,
            Keycode.DpadDown => Key.Down,
            Keycode.DpadLeft => Key.Left,
            Keycode.DpadRight => Key.Right,
            Keycode.ShiftLeft => Key.ShiftLeft,
            Keycode.ShiftRight => Key.ShiftRight,
            Keycode.CtrlLeft => Key.ControlLeft,
            Keycode.CtrlRight => Key.ControlRight,
            Keycode.AltLeft => Key.AltLeft,
            Keycode.AltRight => Key.AltRight,
            Keycode.MetaLeft => Key.SuperLeft,
            Keycode.MetaRight => Key.SuperRight,
            Keycode.CapsLock => Key.CapsLock,
            Keycode.ScrollLock => Key.ScrollLock,
            Keycode.NumLock => Key.NumLock,
            Keycode.Sysrq => Key.PrintScreen,
            Keycode.Break => Key.Pause,
            Keycode.Menu => Key.Menu,
            Keycode.Comma => Key.Comma,
            Keycode.Period => Key.Period,
            Keycode.Minus => Key.Minus,
            (Keycode)70 => Key.Equal, // KEYCODE_EQUALS, named Equals in the binding which reads like object.Equals
            Keycode.LeftBracket => Key.LeftBracket,
            Keycode.RightBracket => Key.RightBracket,
            Keycode.Backslash => Key.BackSlash,
            Keycode.Semicolon => Key.Semicolon,
            Keycode.Apostrophe => Key.Apostrophe,
            Keycode.Slash => Key.Slash,
            Keycode.Grave => Key.GraveAccent,
            Keycode.NumpadDot => Key.KeypadDecimal,
            Keycode.NumpadDivide => Key.KeypadDivide,
            Keycode.NumpadMultiply => Key.KeypadMultiply,
            Keycode.NumpadSubtract => Key.KeypadSubtract,
            Keycode.NumpadAdd => Key.KeypadAdd,
            Keycode.NumpadEnter => Key.KeypadEnter,
            Keycode.NumpadEquals => Key.KeypadEqual,
            _ => null
        };
    }

    /// <summary>The modifiers held as a key event says, which is a snapshot Android keeps for us.</summary>
    public static KeyModifiers Modifiers(KeyEvent key) =>
        (key.IsShiftPressed ? KeyModifiers.Shift : 0) |
        (key.IsCtrlPressed ? KeyModifiers.Control : 0) |
        (key.IsAltPressed ? KeyModifiers.Alt : 0) |
        (key.IsMetaPressed ? KeyModifiers.Super : 0);

    /// <summary>What a key event types, null for nothing (a modifier, a control key, a dead key on its own, Ctrl held).</summary>
    public static string? Text(KeyEvent key)
    {
        // Ctrl and Meta chords are shortcuts, not typing, the same as GLFW's char callback never hearing them
        if (key.IsCtrlPressed || key.IsMetaPressed) return null;

        int unicode = key.GetUnicodeChar(key.MetaState);

        // KeyCharacterMap.COMBINING_ACCENT is the top bit, a dead key waiting for the next one
        if (unicode <= 0 || (unicode & unchecked((int)0x80000000)) != 0) return null;
        if (unicode < 0x20 || unicode == 0x7F) return null;
        return char.ConvertFromUtf32(unicode);
    }
}
