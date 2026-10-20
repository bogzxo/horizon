using System.Collections.Concurrent;

namespace Horizon.UI;

/// <summary>
/// How a key that is written "Ctrl+Shift+S" (in a menu, a key file, a tooltip) is shown on the machine it runs on. Ctrl
/// in a shortcut is the platform's key for shortcuts (Horizon.Input.Keyboard.Primary), which on a Mac is command, so
/// there it says Cmd, and Alt is the key a Mac calls Option. Files go on saying Ctrl, the same file works everywhere.
/// </summary>
public static class UIShortcutText
{
    private static readonly ConcurrentDictionary<string, string> shown = new(StringComparer.Ordinal);

    /// <summary>A shortcut as written, as this machine's keyboard says it.</summary>
    public static string Shown(string written)
    {
        if (!Horizon.Input.Keyboard.PrimaryIsCommand || written.Length == 0)
            return written;

        return shown.GetOrAdd(written, static text =>
        {
            var parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                parts[i] = parts[i].Trim().ToLowerInvariant() switch
                {
                    "ctrl" or "control" => "Cmd",
                    "alt" => "Option",
                    "super" or "win" or "cmd" => "Cmd",
                    "rawctrl" => "Ctrl",
                    _ => parts[i]
                };
            }

            return string.Join('+', parts);
        });
    }
}
