namespace Horizon.UI.Components;

/// <summary>
/// The histories the shells keep themselves, read for a search over every line ever run: PSReadLine's
/// ConsoleHost_history.txt, bash's .bash_history, zsh's .zsh_history (its timestamps taken off) and fish's
/// fish_history (its YAML of cmd lines). Read every time it is asked for, they are the shells' and change under it.
/// </summary>
public static class TerminalShellHistory
{
    /// <summary>The files the shells keep their histories in, the ones there are.</summary>
    public static IEnumerable<string> Files()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        [
            Path.Combine(appData, "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt"),
            Path.Combine(home, ".local", "share", "powershell", "PSReadLine", "ConsoleHost_history.txt"),
            Path.Combine(home, ".bash_history"),
            Path.Combine(home, ".zsh_history"),
            Path.Combine(home, ".local", "share", "fish", "fish_history")
        ];

        foreach (string file in candidates)
        {
            if (File.Exists(file))
                yield return file;
        }
    }

    /// <summary>The lines of one history file as they were run, the oldest first.</summary>
    public static List<string> Read(string file, string text)
    {
        var lines = new List<string>();
        string name = Path.GetFileName(file);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (name == "fish_history")
            {
                if (!line.StartsWith("- cmd: ", StringComparison.Ordinal))
                    continue;
                line = line[7..];
            }
            else if (name == ".zsh_history" && line.StartsWith(": ", StringComparison.Ordinal) && line.IndexOf(';') is var semicolon and > 0)
            {
                line = line[(semicolon + 1)..];
            }

            // A line PSReadLine kept with a backtick at its end goes on onto the next, the search takes the first of it
            line = line.TrimEnd('`').Trim();
            if (line.Length > 0)
                lines.Add(line);
        }

        return lines;
    }

    /// <summary>Every line of every shell's history and of the terminal's own, the newest first, each once.</summary>
    public static List<string> All(IEnumerable<string> own)
    {
        var all = new List<string>();
        foreach (string file in Files())
        {
            try
            {
                all.AddRange(Read(file, File.ReadAllText(file)));
            }
            catch (IOException)
            {
                // A shell writing it right now, it is read the next time
            }
        }

        all.AddRange(own);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newestFirst = new List<string>();
        for (int i = all.Count - 1; i >= 0; i--)
        {
            if (seen.Add(all[i]))
                newestFirst.Add(all[i]);
        }

        return newestFirst;
    }
}
