using Silk.NET.Input;

namespace Horizon.UI;

/// <summary>
/// Keys as the menus write them, "Ctrl+Shift+F", read into the key and what's held with it and written back the same
/// way, for a file of keys somebody changed and the keys a script puts its commands on. Ctrl is command on a Mac
/// (Keyboard.Primary), a file says Ctrl either way and the menus show Cmd there (UIShortcutText).
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

    /// <summary>A text as the menus would write it, the same text for the same key however it was written, null for one that isn't a key.</summary>
    public static string? Tidy(string written) => Read(written) is { } read ? Write(read.Key, read.Control, read.Shift, read.Alt) : null;

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
