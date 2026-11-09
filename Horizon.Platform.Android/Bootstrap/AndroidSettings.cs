using Android.Content;

namespace Horizon.Platform.Android;

/// <summary>
/// The HORIZON_* settings of a run on a phone, where a desktop would have them in its environment. They come from a
/// horizon.env in the app's files folder (one NAME=value a line, # for a comment) and then from the extras of the
/// intent that started the activity, which win.
/// <code>
/// adb shell am start -n dev.horizon.testing/dev.horizon.testing.TestingActivity --es HORIZON_SCREENSHOT shot.png@3
/// </code>
/// Only names that start with HORIZON_ are taken, and they're put in this process's own environment
/// (Environment.SetEnvironmentVariable), which nothing outside the process ever sees. Nothing global is written.
/// </summary>
public static class AndroidSettings
{
    /// <summary>The file in the files folder that's read, see the class remarks.</summary>
    public const string FILE = "horizon.env";

    private const string PREFIX = "HORIZON_";

    /// <summary>Reads horizon.env, if there is one, into name and value. A line it can't make sense of is a problem, not an error.</summary>
    public static Dictionary<string, string> ReadFile(string path, List<string> problems)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return settings;

        int number = 0;
        foreach (string raw in File.ReadLines(path))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            int equals = line.IndexOf('=');
            string name = equals > 0 ? line[..equals].Trim() : string.Empty;
            if (!IsSetting(name))
            {
                problems.Add($"{FILE} line {number} isn't a HORIZON_NAME=value line, it's skipped.");
                continue;
            }

            settings[name] = Unquote(line[(equals + 1)..].Trim());
        }

        return settings;
    }

    /// <summary>The HORIZON_* string extras of an intent. Extras of another type are a problem, not a setting.</summary>
    public static Dictionary<string, string> ReadExtras(Intent? intent, List<string> problems)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (intent?.Extras is not { } extras) return settings;

        foreach (string name in extras.KeySet() ?? [])
        {
            if (!IsSetting(name)) continue;

            // --es gives a string, anything else (--ei, --ez) has no string of its own and is said so rather than guessed at
            if (extras.GetString(name) is { } value) settings[name] = value;
            else problems.Add($"The intent extra {name} isn't a string, pass it with --es.");
        }

        return settings;
    }

    /// <summary>Whether a name is one of the settings taken.</summary>
    public static bool IsSetting(string name) =>
        name.Length > PREFIX.Length && name.StartsWith(PREFIX, StringComparison.Ordinal) && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'') ? value[1..^1] : value;
}
