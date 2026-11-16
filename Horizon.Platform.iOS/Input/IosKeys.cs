using Horizon.Input;

using UIKit;

namespace Horizon.Platform.iOS;

/// <summary>
/// A hardware keyboard's keys (a Magic Keyboard on an iPad, a Bluetooth one, the Mac's own in the simulator) as the
/// engine's keys. UIKit names them by their USB HID usage, which never changes, so ranges of them are mapped by
/// arithmetic. A key the engine has no name for is null and goes no further.
/// </summary>
internal static class IosKeys
{
    public static Key? Of(UIKeyboardHidUsage usage)
    {
        int value = (int)usage;

        // A 4 to Z 29, 1 30 to 9 38 and 0 39, F1 58 to F12 69, F13 104 to F24 115, keypad 1 89 to 9 97 and 0 98
        if (value is >= 4 and <= 29) return (Key)((int)Key.A + value - 4);
        if (value is >= 30 and <= 38) return (Key)((int)Key.Number1 + value - 30);
        if (value is >= 58 and <= 69) return (Key)((int)Key.F1 + value - 58);
        if (value is >= 104 and <= 115) return (Key)((int)Key.F13 + value - 104);
        if (value is >= 89 and <= 97) return (Key)((int)Key.Keypad1 + value - 89);

        return value switch
        {
            39 => Key.Number0,
            40 => Key.Enter,
            41 => Key.Escape,
            42 => Key.Backspace,
            43 => Key.Tab,
            44 => Key.Space,
            45 => Key.Minus,
            46 => Key.Equal,
            47 => Key.LeftBracket,
            48 => Key.RightBracket,
            49 => Key.BackSlash,
            51 => Key.Semicolon,
            52 => Key.Apostrophe,
            53 => Key.GraveAccent,
            54 => Key.Comma,
            55 => Key.Period,
            56 => Key.Slash,
            57 => Key.CapsLock,
            70 => Key.PrintScreen,
            71 => Key.ScrollLock,
            72 => Key.Pause,
            73 => Key.Insert,
            74 => Key.Home,
            75 => Key.PageUp,
            76 => Key.Delete,
            77 => Key.End,
            78 => Key.PageDown,
            79 => Key.Right,
            80 => Key.Left,
            81 => Key.Down,
            82 => Key.Up,
            83 => Key.NumLock,
            84 => Key.KeypadDivide,
            85 => Key.KeypadMultiply,
            86 => Key.KeypadSubtract,
            87 => Key.KeypadAdd,
            88 => Key.KeypadEnter,
            98 => Key.Keypad0,
            99 => Key.KeypadDecimal,
            100 => Key.World1,
            101 => Key.Menu,
            103 => Key.KeypadEqual,
            224 => Key.ControlLeft,
            225 => Key.ShiftLeft,
            226 => Key.AltLeft,
            227 => Key.SuperLeft,
            228 => Key.ControlRight,
            229 => Key.ShiftRight,
            230 => Key.AltRight,
            231 => Key.SuperRight,
            _ => null
        };
    }

    /// <summary>The modifiers held with a key, Command as Super the way a Mac's is.</summary>
    public static KeyModifiers Modifiers(UIKeyModifierFlags flags)
    {
        var modifiers = KeyModifiers.None;
        if (flags.HasFlag(UIKeyModifierFlags.Shift)) modifiers |= KeyModifiers.Shift;
        if (flags.HasFlag(UIKeyModifierFlags.Control)) modifiers |= KeyModifiers.Control;
        if (flags.HasFlag(UIKeyModifierFlags.Alternate)) modifiers |= KeyModifiers.Alt;
        if (flags.HasFlag(UIKeyModifierFlags.Command)) modifiers |= KeyModifiers.Super;
        return modifiers;
    }
}
