using System.Text;

namespace Horizon.Platform;

/// <summary>A key as an NSMenuItem has it, the text of <c>keyEquivalent</c> and the <c>keyEquivalentModifierMask</c> held with it.</summary>
public readonly record struct MacKeyEquivalent(string Key, ulong Modifiers);

/// <summary>
/// Keys as a program writes them ("Ctrl+Shift+P") turned into what a Mac's menu item takes, pure text in and text out so it
/// can be tested anywhere. Ctrl is Command (what <c>Keyboard.PrimaryIsCommand</c> says it is on a Mac), "RawCtrl" is the
/// Control key itself, Alt is Option. Two keys one after the other (a chord, "Ctrl+K Z") are no key equivalent at all, a
/// menu item can only have one.
/// </summary>
public static class MacKeyEquivalents
{
    // NSEventModifierFlag
    public const ulong Shift = 1ul << 17, Control = 1ul << 18, Option = 1ul << 19, Command = 1ul << 20;

    // The characters the function keys are in a keyEquivalent (NSFunctionKey and the control characters)
    public const string Up = "", Down = "", Left = "", Right = "", Insert = "", Delete = "",
        Home = "", End = "", PageUp = "", PageDown = "";

    /// <summary>The key equivalent of a key as it's written, null for no key, a chord, or one a menu has no way of writing.</summary>
    public static MacKeyEquivalent? Map(string? written)
    {
        if (string.IsNullOrWhiteSpace(written))
            return null;

        if (!Read(written, out bool command, out bool control, out bool option, out bool shift, out string? key, out bool chord) || chord || key is null)
            return null;

        string? character = CharacterOf(key);
        if (character is null)
            return null;

        // A letter with shift is the capital, which AppKit takes as shift held
        bool letter = character.Length == 1 && char.IsAsciiLetter(character[0]);
        if (letter && shift)
            character = character.ToUpperInvariant();

        ulong mask = (command ? Command : 0) | (control ? Control : 0) | (option ? Option : 0) | (shift ? Shift : 0);
        return new MacKeyEquivalent(character, mask);
    }

    /// <summary>Whether a text is two keys one after the other.</summary>
    public static bool IsChord(string? written) =>
        !string.IsNullOrWhiteSpace(written) && Read(written, out _, out _, out _, out _, out _, out bool chord) && chord;

    /// <summary>
    /// What a key says with the Mac's own signs ("⌃⇧⌥⌘" in front of the key, in the order a Mac writes them), a chord with
    /// its two keys. For a menu item whose key can't be a key equivalent (a chord), to say it in its title.
    /// </summary>
    public static string Glyphs(string? written)
    {
        if (string.IsNullOrWhiteSpace(written))
            return string.Empty;

        var text = new StringBuilder();
        foreach (string stroke in Strokes(written))
        {
            if (text.Length > 0)
                text.Append(' ');

            var parts = stroke.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            bool command = false, control = false, option = false, shift = false;
            string key = string.Empty;
            foreach (string part in parts)
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl" or "control" or "cmd" or "command": command = true; break;
                    case "rawctrl": control = true; break;
                    case "alt" or "option" or "opt": option = true; break;
                    case "shift": shift = true; break;
                    default: key = part.Length == 1 ? part.ToUpperInvariant() : part; break;
                }
            }

            text.Append(control ? "⌃" : string.Empty).Append(option ? "⌥" : string.Empty).Append(shift ? "⇧" : string.Empty)
                .Append(command ? "⌘" : string.Empty).Append(key);
        }

        return text.ToString();
    }

    // Splits a written key into its modifiers and the one key, false for a text with no key or two
    private static bool Read(string written, out bool command, out bool control, out bool option, out bool shift, out string? key, out bool chord)
    {
        command = control = option = shift = chord = false;
        key = null;

        string[] strokes = Strokes(written);
        if (strokes.Length == 0 || strokes.Length > 2)
            return false;

        chord = strokes.Length == 2;
        foreach (string part in strokes[0].Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" or "cmd" or "command": command = true; break;
                case "rawctrl": control = true; break;
                case "alt" or "option" or "opt": option = true; break;
                case "shift": shift = true; break;
                default:
                    if (key is not null)
                        return false;
                    key = part;
                    break;
            }
        }

        return key is not null;
    }

    // The keys of a chord, a gap round a plus isn't a gap between keys ("ctrl + alt + b" is one key)
    private static string[] Strokes(string written)
    {
        var tight = new StringBuilder(written.Length);
        for (int i = 0; i < written.Length; i++)
        {
            char at = written[i];
            if (char.IsWhiteSpace(at) && (NextSolid(written, i) == '+' || LastSolid(tight) == '+'))
                continue;

            tight.Append(at);
        }

        return tight.ToString().Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static char NextSolid(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return text[i];
        }

        return '\0';
    }

    private static char LastSolid(StringBuilder text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return text[i];
        }

        return '\0';
    }

    // The text a key is in a keyEquivalent, null for a key a menu can't have
    private static string? CharacterOf(string key)
    {
        switch (key.ToLowerInvariant())
        {
            case "up": return Up;
            case "down": return Down;
            case "left": return Left;
            case "right": return Right;
            case "insert": return Insert;
            case "delete" or "del": return Delete;
            case "backspace" or "back": return "\b";
            case "home": return Home;
            case "end": return End;
            case "pageup": return PageUp;
            case "pagedown": return PageDown;
            case "escape" or "esc": return "\u001B";
            case "return" or "enter": return "\r";
            case "tab": return "\t";
            case "space": return " ";
        }

        // F1 to F35 are NSF1FunctionKey on, which are 0xF704 and up
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out int function) && function is >= 1 and <= 35)
            return char.ConvertFromUtf32(0xF704 + function - 1);

        if (key.Length == 1)
        {
            char at = key[0];
            if (char.IsAsciiLetter(at))
                return char.ToLowerInvariant(at).ToString();
            if (char.IsAsciiDigit(at) || char.IsPunctuation(at) || char.IsSymbol(at))
                return key;
        }

        // A key written as the one Silk calls it: Number1, Comma, Minus and the others the menus write as a mark
        return key.ToLowerInvariant() switch
        {
            "minus" => "-",
            "equal" => "=",
            "comma" => ",",
            "period" => ".",
            "slash" => "/",
            "backslash" => "\\",
            "semicolon" => ";",
            "apostrophe" => "'",
            "leftbracket" => "[",
            "rightbracket" => "]",
            "graveaccent" => "`",
            var number when number.StartsWith("number", StringComparison.Ordinal) && number.Length == 7 && char.IsAsciiDigit(number[6]) => number[6].ToString(),
            _ => null,
        };
    }
}
