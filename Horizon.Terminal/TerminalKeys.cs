using System.Text;

using Horizon.Input;

namespace Horizon.UI.Components;

/// <summary>
/// What a key is to the program in a terminal, the bytes a terminal sends for it. The old way for the old keys (an
/// arrow is ESC [ A, or ESC O A when the program asked for application cursor keys, with the modifiers the xterm way,
/// ESC [ 1 ; 5 A for control), control and a letter the C0 byte, alt an ESC in front. And the kitty keyboard protocol
/// when the program pushed its flags (CSI > 1 u and up, which vim, neovim, fish and kitty's own programs do), where
/// every key that was ambiguous before comes out as CSI code ; modifiers u, so escape is told from the start of a
/// sequence and control with i from tab. sw.kovidgoyal.net/kitty/keyboard-protocol has the whole of it, this is the
/// disambiguate level and the report of alternate keys, which is what the programs that ask for it use.
/// </summary>
public static class TerminalKeys
{
    /// <summary>The modifiers of a press, the bits the protocol counts them in.</summary>
    [Flags]
    public enum Modifiers { None = 0, Shift = 1, Alt = 2, Control = 4, Super = 8 }

    /// <summary>
    /// The bytes for a key that isn't a printable character, null for a key that sends nothing. Printable characters go
    /// through <see cref="Character"/>.
    /// </summary>
    public static string? Key(Horizon.Input.Key key, Modifiers modifiers, bool applicationCursorKeys, int keyboardFlags)
    {
        bool kitty = (keyboardFlags & 1) != 0;
        int mods = (int)modifiers + 1;

        // The keys with a code of their own in the protocol, the escape and the editing keys
        if (kitty)
        {
            int? code = key switch
            {
                Horizon.Input.Key.Escape => 27,
                Horizon.Input.Key.Enter or Horizon.Input.Key.KeypadEnter => 13,
                Horizon.Input.Key.Tab => 9,
                Horizon.Input.Key.Backspace => 127,
                _ => null
            };
            if (code is { } c)
                return modifiers == Modifiers.None ? Legacy(key, modifiers, applicationCursorKeys) : $"\u001b[{c};{mods}u";
        }

        return Legacy(key, modifiers, applicationCursorKeys);
    }

    private static string? Legacy(Horizon.Input.Key key, Modifiers modifiers, bool applicationCursorKeys)
    {
        int mods = (int)modifiers + 1;
        bool plain = modifiers == Modifiers.None;
        bool alt = (modifiers & Modifiers.Alt) != 0;
        bool control = (modifiers & Modifiers.Control) != 0;
        bool shift = (modifiers & Modifiers.Shift) != 0;

        switch (key)
        {
            case Horizon.Input.Key.Enter or Horizon.Input.Key.KeypadEnter: return alt ? "\u001b\r" : "\r";
            case Horizon.Input.Key.Tab: return shift ? "\u001b[Z" : "\t";
            case Horizon.Input.Key.Backspace: return control ? "\b" : alt ? "\u001b\u007f" : "\u007f";
            case Horizon.Input.Key.Escape: return alt ? "\u001b\u001b" : "\u001b";
            case Horizon.Input.Key.Space when control: return "\0";
        }

        // The arrows and home and end, ESC [ A or ESC O A plain, ESC [ 1 ; m A with modifiers
        char? letter = key switch
        {
            Horizon.Input.Key.Up => 'A',
            Horizon.Input.Key.Down => 'B',
            Horizon.Input.Key.Right => 'C',
            Horizon.Input.Key.Left => 'D',
            Horizon.Input.Key.Home => 'H',
            Horizon.Input.Key.End => 'F',
            _ => null
        };
        if (letter is { } l)
            return plain ? (applicationCursorKeys ? $"\u001bO{l}" : $"\u001b[{l}") : $"\u001b[1;{mods}{l}";

        // The function keys, F1 to F4 the old SS3 way plain
        char? ss3 = key switch
        {
            Horizon.Input.Key.F1 => 'P',
            Horizon.Input.Key.F2 => 'Q',
            Horizon.Input.Key.F3 => 'R',
            Horizon.Input.Key.F4 => 'S',
            _ => null
        };
        if (ss3 is { } s)
            return plain ? $"\u001bO{s}" : $"\u001b[1;{mods}{s}";

        int? tilde = key switch
        {
            Horizon.Input.Key.Insert => 2,
            Horizon.Input.Key.Delete => 3,
            Horizon.Input.Key.PageUp => 5,
            Horizon.Input.Key.PageDown => 6,
            Horizon.Input.Key.F5 => 15,
            Horizon.Input.Key.F6 => 17,
            Horizon.Input.Key.F7 => 18,
            Horizon.Input.Key.F8 => 19,
            Horizon.Input.Key.F9 => 20,
            Horizon.Input.Key.F10 => 21,
            Horizon.Input.Key.F11 => 23,
            Horizon.Input.Key.F12 => 24,
            _ => null
        };
        if (tilde is { } t)
            return plain ? $"\u001b[{t}~" : $"\u001b[{t};{mods}~";

        // Control and a letter or one of the few punctuation keys that have a C0 code, alt puts an ESC in front
        if (control)
        {
            int? c0 = key switch
            {
                >= Horizon.Input.Key.A and <= Horizon.Input.Key.Z => key - Horizon.Input.Key.A + 1,
                Horizon.Input.Key.LeftBracket => 27,
                Horizon.Input.Key.BackSlash => 28,
                Horizon.Input.Key.RightBracket => 29,
                Horizon.Input.Key.Number2 => 0,
                Horizon.Input.Key.Number6 => 30,
                Horizon.Input.Key.Minus => 31,
                Horizon.Input.Key.Slash => 31,
                _ => null
            };
            if (c0 is { } code)
                return alt ? "\u001b" + (char)code : ((char)code).ToString();
        }

        return null;
    }

    /// <summary>
    /// The bytes for a character that was typed, the character itself, with an ESC in front when alt was held, or
    /// in the protocol's form when it asked for it and a modifier other than shift is down.
    /// </summary>
    public static string Character(string text, Modifiers modifiers, int keyboardFlags)
    {
        bool alt = (modifiers & Modifiers.Alt) != 0;
        if ((keyboardFlags & 1) != 0 && (modifiers & (Modifiers.Alt | Modifiers.Control | Modifiers.Super)) != 0 && text.Length > 0)
        {
            int code = char.ConvertToUtf32(text, 0);
            return $"\u001b[{(int)char.ToLowerInvariant((char)code)};{(int)modifiers + 1}u";
        }

        return alt ? "\u001b" + text : text;
    }

    /// <summary>A press, release or drag of a mouse button at a cell, the way the program asked, X10 or SGR.</summary>
    /// <param name="button">0 left, 1 middle, 2 right, 3 release (X10 only), 64 wheel up, 65 wheel down.</param>
    public static string Mouse(int button, int column, int row, Modifiers modifiers, bool pressed, bool motion, bool sgr)
    {
        int code = button + ((modifiers & Modifiers.Shift) != 0 ? 4 : 0) + ((modifiers & Modifiers.Alt) != 0 ? 8 : 0) + ((modifiers & Modifiers.Control) != 0 ? 16 : 0) + (motion ? 32 : 0);
        if (sgr)
            return $"\u001b[<{code};{column + 1};{row + 1}{(pressed ? 'M' : 'm')}";

        if (!pressed)
            code = (code & ~3) | 3;
        var bytes = new StringBuilder("\u001b[M");
        bytes.Append((char)(32 + code));
        bytes.Append((char)Math.Min(255, 33 + column));
        bytes.Append((char)Math.Min(255, 33 + row));
        return bytes.ToString();
    }

    /// <summary>What a paste looks like to a program that asked for bracketed paste, the text between its two marks.</summary>
    public static string Paste(string text, bool bracketed) => bracketed ? "\u001b[200~" + text + "\u001b[201~" : text;
}
