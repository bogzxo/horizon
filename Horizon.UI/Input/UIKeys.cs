using Silk.NET.Input;

namespace Horizon.UI;

/// <summary>One press of a key with what's held with it, a key on its own or either half of a chord.</summary>
public readonly record struct UIKeyStroke(Key Key, bool Control, bool Shift, bool Alt)
{
    /// <summary>The way the menus write it, "Ctrl+K".</summary>
    public override string ToString() => UIKeys.Write(Key, Control, Shift, Alt);
}

/// <summary>
/// Keys as the menus write them, "Ctrl+Shift+F", read into the key and what's held with it and written back the same
/// way, for a file of keys somebody changed and the keys a script puts its commands on. Ctrl is command on a Mac
/// (Keyboard.Primary), a file says Ctrl either way and the menus show Cmd there (UIShortcutText). Two keys pressed one
/// after the other, VS Code's chords, are written with a gap between them ("Ctrl+K Z"), see <see cref="ReadChord"/>.
/// </summary>
public static class UIKeys
{
    // The keys that are written as what they type, or shorter than Silk calls them
    private static readonly (string Written, Key Key)[] Named =
    [
        ("Del", Key.Delete), ("Esc", Key.Escape), ("-", Key.Minus), ("=", Key.Equal), ("/", Key.Slash), ("\\", Key.BackSlash),
        ("[", Key.LeftBracket), ("]", Key.RightBracket), (",", Key.Comma), (".", Key.Period), (";", Key.Semicolon),
        ("'", Key.Apostrophe), ("`", Key.GraveAccent)
    ];

    /// <summary>The key and what's held with it, null for a text that isn't one.</summary>
    public static (Key Key, bool Control, bool Shift, bool Alt)? Read(string written)
    {
        bool control = false, shift = false, alt = false;
        Key? key = null;
        foreach (string part in written.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" or "cmd" or "command": control = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                default:
                    if (key is not null)
                        return null;
                    key = KeyCalled(part);
                    if (key is null)
                        return null;
                    break;
            }
        }

        return key is { } found ? (found, control, shift, alt) : null;
    }

    /// <summary>The way the menus write a key, control first, then shift, then alt.</summary>
    public static string Write(Key key, bool control, bool shift, bool alt) =>
        (control ? "Ctrl+" : string.Empty) + (shift ? "Shift+" : string.Empty) + (alt ? "Alt+" : string.Empty) + NameOf(key);

    /// <summary>
    /// A text as the menus would write it, the same text for the same key however it was written, null for one that isn't
    /// a key. A chord comes back a chord, "ctrl+k z" is "Ctrl+K Z".
    /// </summary>
    public static string? Tidy(string written) => ReadChord(written) is { } read ? WriteChord(read.First, read.Second) : null;

    /// <summary>
    /// A key, or two pressed one after the other the way VS Code has them ("Ctrl+K Ctrl+S", a gap between the two), the
    /// first and the second if there is one. Null for a text that isn't one or two keys.
    /// </summary>
    public static (UIKeyStroke First, UIKeyStroke? Second)? ReadChord(string written)
    {
        string[] strokes = Strokes(written);
        if (strokes.Length is 0 or > 2 || Read(strokes[0]) is not { } first)
            return null;

        var one = new UIKeyStroke(first.Key, first.Control, first.Shift, first.Alt);
        if (strokes.Length == 1)
            return (one, null);

        return Read(strokes[1]) is { } second ? (one, new UIKeyStroke(second.Key, second.Control, second.Shift, second.Alt)) : null;
    }

    /// <summary>A chord the way the menus write it, the two keys with a gap between, or the one key on its own.</summary>
    public static string WriteChord(UIKeyStroke first, UIKeyStroke? second) =>
        second is { } then ? $"{first} {then}" : first.ToString();

    /// <summary>Whether a text is two keys one after the other and not one.</summary>
    public static bool IsChord(string written) => ReadChord(written) is { Second: not null };

    /// <summary>The first key of a chord as the menus write it, the key itself for one that isn't a chord, null for no key.</summary>
    public static string? FirstOf(string written) => ReadChord(written) is { } read ? read.First.ToString() : null;

    // The keys of a chord, the gaps round a plus aren't a gap between two keys ("ctrl + alt + b" is one)
    private static string[] Strokes(string written)
    {
        var tight = new System.Text.StringBuilder(written.Length);
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

    private static char LastSolid(System.Text.StringBuilder text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
                return text[i];
        }

        return '\0';
    }

    /// <summary>Whether it's a key held with others rather than pressed (shift, control, alt and the windows key).</summary>
    public static bool IsHeld(Key key) => key is Key.ShiftLeft or Key.ShiftRight or Key.ControlLeft or Key.ControlRight
        or Key.AltLeft or Key.AltRight or Key.SuperLeft or Key.SuperRight or Key.Unknown;

    private static Key? KeyCalled(string part)
    {
        foreach (var (written, key) in Named)
        {
            if (written.Equals(part, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        if (part.Length == 1 && char.IsDigit(part[0]))
            return Key.Number0 + (part[0] - '0');

        return Enum.TryParse(part, ignoreCase: true, out Key parsed) && Enum.IsDefined(parsed) && !int.TryParse(part, out _) ? parsed : null;
    }

    private static string NameOf(Key key)
    {
        foreach (var (written, named) in Named)
        {
            if (named == key)
                return written;
        }

        return key is >= Key.Number0 and <= Key.Number9 ? ((char)('0' + (key - Key.Number0))).ToString() : key.ToString();
    }
}
