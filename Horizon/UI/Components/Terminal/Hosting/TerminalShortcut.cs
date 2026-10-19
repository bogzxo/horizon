using Silk.NET.Input;

namespace Horizon.UI.Components;

/// <summary>
/// Keys as a keys file writes them, "Ctrl+Shift+T", read into the key and what is held with it and written back the
/// same way whatever way they were written. Ctrl, Shift and Alt in any order and the key last, a letter, a digit, F1
/// to F12, the arrows by name, PgUp and PgDn, Home, End, Tab, Enter, Esc, Del, and the punctuation as itself.
/// </summary>
public static class TerminalShortcut
{
    // The keys written as what they type, or shorter than Silk calls them
    private static readonly (string Written, Key Key)[] Named =
    [
        ("Del", Key.Delete), ("Esc", Key.Escape), ("-", Key.Minus), ("=", Key.Equal), ("/", Key.Slash), ("\\", Key.BackSlash),
        ("[", Key.LeftBracket), ("]", Key.RightBracket), (",", Key.Comma), (".", Key.Period), (";", Key.Semicolon),
        ("'", Key.Apostrophe), ("`", Key.GraveAccent), ("PgUp", Key.PageUp), ("PgDn", Key.PageDown)
    ];

    /// <summary>The key and what is held with it, null for a text that isn't one.</summary>
    public static (Key Key, bool Control, bool Shift, bool Alt)? Read(string written)
    {
        bool control = false, shift = false, alt = false;
        Key? key = null;

        // A plus on its own at the end is the key itself, Ctrl+Shift++ would be read as nothing otherwise
        string[] parts = written.EndsWith("++", StringComparison.Ordinal)
            ? [.. written[..^2].Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), "="]
            : written.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": control = true; break;
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

    /// <summary>Whether a press is the key a text names, held exactly so.</summary>
    public static bool Matches(in UIKeyPress press, (Key Key, bool Control, bool Shift, bool Alt) key) =>
        press.Key == key.Key && press.Control == key.Control && press.Shift == key.Shift && press.Alt == key.Alt;

    /// <summary>The way the menus write a key, control first, then shift, then alt.</summary>
    public static string Write(Key key, bool control, bool shift, bool alt) =>
        (control ? "Ctrl+" : string.Empty) + (shift ? "Shift+" : string.Empty) + (alt ? "Alt+" : string.Empty) + NameOf(key);

    /// <summary>A text as the menus would write it, null for one that isn't a key.</summary>
    public static string? Tidy(string written) => Read(written) is { } read ? Write(read.Key, read.Control, read.Shift, read.Alt) : null;

    /// <summary>Every name a key can be written with, for completing one in an editor.</summary>
    public static IEnumerable<string> KeyNames()
    {
        foreach (var (written, _) in Named)
            yield return written;
        for (char letter = 'A'; letter <= 'Z'; letter++)
            yield return letter.ToString();
        for (char digit = '0'; digit <= '9'; digit++)
            yield return digit.ToString();
        for (int f = 1; f <= 12; f++)
            yield return "F" + f;
        foreach (string name in new[] { "Up", "Down", "Left", "Right", "Home", "End", "Tab", "Enter", "Space", "Backspace", "Insert" })
            yield return name;
    }

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
