using System.Collections.Concurrent;
using System.Text;

namespace Horizon.UI;

/// <summary>
/// How a key that is written "Ctrl+Shift+S" (in a menu, a key file, a tooltip) is shown on the machine it runs on. Ctrl
/// in a shortcut is the platform's key for shortcuts (Horizon.Input.Keyboard.Primary), which on a Mac is command, so
/// there it is the way a Mac writes it: ⇧⌘S, the keys held in Apple's order (control, option, shift, command) with no
/// plus between them, and the caret keys as their symbols (↩ ⌫ ⌦ ⎋ ⇥ ← → ↑ ↓ ⇞ ⇟ ↖ ↘). Alt is the key a Mac calls Option. Files
/// go on saying Ctrl, the same file works everywhere. Elsewhere the text is shown as it was written. What is shown is
/// for the eye only, nothing reads it back.
/// </summary>
public static class UIShortcutText
{
    private static readonly ConcurrentDictionary<string, string> shown = new(StringComparer.Ordinal);

    /// <summary>A shortcut as written, as this machine's keyboard says it.</summary>
    public static string Shown(string written) => Shown(written, Horizon.Input.Keyboard.PrimaryIsCommand);

    /// <summary>A shortcut as written, as a Mac's keyboard says it (mac) or as it was written. On its own so it can be tested anywhere.</summary>
    public static string Shown(string written, bool mac)
    {
        if (!mac || written.Length == 0)
            return written;

        // Each key of a chord on its own, "Ctrl+K Ctrl+S" is ⌘K ⌘S
        return shown.GetOrAdd(written, static text => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stroke)));
    }

    // One key and what's held with it, the held ones as a Mac's glyphs
    private static string Stroke(string text)
    {
        // A plus is the key itself when it ends the stroke ("Ctrl++")
        string key, held;
        if (text.Length > 1 && text[^1] == '+')
        {
            key = "+";
            held = text[..^1];
        }
        else
        {
            int last = text.LastIndexOf('+');
            key = last < 0 ? text : text[(last + 1)..];
            held = last < 0 ? string.Empty : text[..last];
        }

        bool control = false, option = false, shift = false, command = false;
        foreach (string part in held.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control" or "super" or "win" or "cmd" or "command": command = true; break;
                case "alt" or "option": option = true; break;
                case "shift": shift = true; break;
                case "rawctrl": control = true; break;
            }
        }

        var built = new StringBuilder(8);
        if (control) built.Append('⌃');
        if (option) built.Append('⌥');
        if (shift) built.Append('⇧');
        if (command) built.Append('⌘');
        return built.Append(KeyName(key.Trim())).ToString();
    }

    // A key that has a symbol of its own on a Mac's keyboard, the others as they were written
    private static string KeyName(string key) => key.ToLowerInvariant() switch
    {
        "enter" or "return" => "↩",
        "backspace" => "⌫",
        "delete" or "del" => "⌦",
        "escape" or "esc" => "⎋",
        "tab" => "⇥",
        "left" => "←",
        "right" => "→",
        "up" => "↑",
        "down" => "↓",
        "pageup" => "⇞",
        "pagedown" => "⇟",
        "home" => "↖",
        "end" => "↘",
        _ => key
    };
}
