using System.Text.RegularExpressions;

namespace Horizon.UI.Components;

/// <summary>Which of the terminal's files a file is.</summary>
public enum TerminalFileKind { None, Settings, Keys, Plugin, Sessions, Api }

/// <summary>Something to write at the caret, what it is, and the text that goes in (the label when that is null).</summary>
public readonly record struct TerminalCompletion(string Label, string Detail, string? Insert = null);

/// <summary>Something wrong with a line of a terminal file, where (0 based) and how long, and what.</summary>
public readonly record struct TerminalFileProblem(int Line, int Column, int Length, string Message, bool Warning = true);

/// <summary>
/// What an editor knows about Dusk's files out of a <see cref="TerminalApiDescription"/>, without the editor. In a
/// settings.hor the keys of the settings and the values a choice takes, in a keys.hor the names of the commands and a
/// key as it is written, in a plugin the functions of the terminal object after <c>terminal.</c> (or <c>dusk.</c>) and
/// the hooks a plugin can have. Each completed, explained under the pointer and checked, a setting that isn't one, a
/// key that isn't a key, a command no plugin registers, a function the object hasn't got. Hex puts it through its
/// completion, hover, signatures and lints, and anything else could.
/// </summary>
public sealed class TerminalFileKnowledge(TerminalApiDescription api)
{
    public TerminalApiDescription Api { get; } = api;

    /// <summary>Which of the terminal's files a file is, by its name, and by its define for one that isn't called the usual.</summary>
    public static TerminalFileKind KindOf(string path, string text)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        bool ours = TerminalFiles.IsTerminalFile(path, text);
        return name switch
        {
            TerminalSettings.FILE when ours || text.Contains("let settings", StringComparison.Ordinal) => TerminalFileKind.Settings,
            TerminalFiles.KEYS when ours || text.Contains("let keys", StringComparison.Ordinal) && text.Contains("#define dusk", StringComparison.Ordinal) => TerminalFileKind.Keys,
            TerminalPlugin.FILE when ours || text.Contains("terminal.command", StringComparison.Ordinal) || text.Contains("dusk.command", StringComparison.Ordinal) => TerminalFileKind.Plugin,
            TerminalFiles.SESSIONS when ours => TerminalFileKind.Sessions,
            TerminalApiDescription.FILE when ours => TerminalFileKind.Api,
            _ when ours && text.Contains("let settings", StringComparison.Ordinal) => TerminalFileKind.Settings,
            _ when ours && text.Contains("let keys", StringComparison.Ordinal) => TerminalFileKind.Keys,
            _ when ours && text.Contains("let plugin", StringComparison.Ordinal) => TerminalFileKind.Plugin,
            _ => TerminalFileKind.None
        };
    }

    // The names a plugin is handed the object by
    private static readonly string[] ObjectNames = ["terminal.", "dusk."];

    /* Completion */

    /// <summary>
    /// What there is to write at the caret, given the line up to it, with the column the completion replaces from.
    /// Null when the file has nothing to say there and the editor's own completion is better.
    /// </summary>
    public (int Start, List<TerminalCompletion> Items)? Complete(TerminalFileKind kind, string before)
    {
        int start = before.Length;
        while (start > 0 && (char.IsLetterOrDigit(before[start - 1]) || before[start - 1] == '_'))
            start--;
        string ahead = before[..start];

        switch (kind)
        {
            case TerminalFileKind.Plugin:
                foreach (string name in ObjectNames)
                {
                    if (ahead.EndsWith(name, StringComparison.Ordinal) && (ahead.Length == name.Length || !char.IsLetterOrDigit(ahead[^(name.Length + 1)])))
                        return (start, [.. Api.Functions.Values.OrderBy(entry => entry.Function.Name, StringComparer.Ordinal)
                            .Select(entry => new TerminalCompletion(entry.Function.Name, Short($"({entry.Function.Parameters}) {entry.Function.Help}{Programs(entry.Programs)}"), entry.Function.Name + "("))]);
                }

                // A hook, where a func is being declared
                if (ahead.TrimEnd().EndsWith("func", StringComparison.Ordinal))
                    return (start, [.. Api.Hooks.Select(hook => new TerminalCompletion(hook.Name, Short(hook.About), $"{hook.Name}({hook.Parameters}) "))]);

                return null;

            case TerminalFileKind.Settings:
                if (InsideText(before, out int quote, out string key))
                {
                    if (Api.Settings.Find(setting => setting.Key == key) is { Choices: { Length: > 0 } choices })
                        return (quote + 1, [.. choices.Select(choice => new TerminalCompletion(choice, key))]);
                    return null;
                }

                if (KeyPlace(ahead))
                    return (start, [.. Api.Settings.Select(setting => new TerminalCompletion(setting.Key, Short($"{setting.Kind}, {setting.Default}. {setting.Help}"), setting.Key + ": "))]);
                return null;

            case TerminalFileKind.Keys:
                if (InsideText(before, out int keyQuote, out _))
                {
                    // A key being written, the modifiers first and the keys after them
                    string written = before[(keyQuote + 1)..];
                    int plus = written.LastIndexOf('+');
                    int from = keyQuote + 1 + plus + 1;
                    var items = new List<TerminalCompletion>();
                    foreach (string modifier in new[] { "Ctrl", "Shift", "Alt" })
                    {
                        if (!written.Contains(modifier + "+", StringComparison.OrdinalIgnoreCase))
                            items.Add(new TerminalCompletion(modifier + "+", "held with the key"));
                    }
                    items.AddRange(TerminalShortcut.KeyNames().Select(name => new TerminalCompletion(name, "the key")));
                    return (from, items);
                }

                if (KeyPlace(ahead))
                    return (start, [.. Api.Commands.Select(command => new TerminalCompletion(command.Key, Short($"{command.Value.Title}{(command.Value.Key.Length > 0 ? $", on {command.Value.Key}" : string.Empty)}"), command.Key + ": \""))]);
                return null;

            default:
                return null;
        }
    }

    private static string Programs(string programs) => programs.Length > 0 ? $" ({programs} only)" : string.Empty;

    private static string Short(string text) => text.Length <= 70 ? text : text[..67] + "...";

    /// <summary>Whether a key of an object goes here, after the brace or a comma, with only spaces since.</summary>
    private static bool KeyPlace(string ahead)
    {
        string trimmed = ahead.TrimEnd();
        return trimmed.Length == 0 || trimmed.EndsWith('{') || trimmed.EndsWith(',');
    }

    /// <summary>Whether the caret is inside a text on its line, where it opened, and the key before the colon ahead of it.</summary>
    private static bool InsideText(string before, out int quote, out string key)
    {
        quote = before.LastIndexOf('"');
        key = string.Empty;
        if (quote < 0 || before.Count(character => character == '"') % 2 == 0)
            return false;

        string head = before[..quote].TrimEnd();
        if (head.EndsWith(':'))
        {
            head = head[..^1].TrimEnd().Trim('"');
            int start = head.Length;
            while (start > 0 && (char.IsLetterOrDigit(head[start - 1]) || head[start - 1] is '_' or ':'))
                start--;
            key = head[start..];
        }

        return true;
    }

    /* What a word is */

    /// <summary>Markdown about the word at a place on a line, null for nothing worth saying.</summary>
    public string? Hover(TerminalFileKind kind, string line, int start, int end)
    {
        if (end <= start)
            return null;

        string word = line[start..end];
        string before = line[..start];
        string after = line[end..].TrimStart();

        if (kind == TerminalFileKind.Plugin)
        {
            foreach (string name in ObjectNames)
            {
                if (before.EndsWith(name, StringComparison.Ordinal))
                {
                    return Api.Functions.TryGetValue(word, out var function)
                        ? $"$(symbol-method) **{name}{word}**({function.Function.Parameters})\n\n{function.Function.Help}{Programs(function.Programs)}"
                        : $"**{word}** isn't something the terminal object has";
                }
            }

            if (Api.Hooks.Find(hook => hook.Name == word) is { Name: not null } found && before.TrimEnd().EndsWith("func", StringComparison.Ordinal))
                return $"$(symbol-event) **{found.Name}**({found.Parameters}), a hook\n\n{found.About}";
            if (word is "terminal" or "dusk")
                return "$(terminal) **" + word + "**, the terminal object a plugin is handed, everything it can do to the program";
        }

        if (kind == TerminalFileKind.Settings && after.StartsWith(':') && Api.Settings.Find(setting => setting.Key == word) is { } described)
            return $"$(settings-gear) **{word}**, {described.Kind}, {described.Default} when it isn't said\n\n{described.Help}"
                + (described.Choices is { Length: > 0 } choices ? $"\n\nOne of {string.Join(", ", choices)}." : string.Empty);

        if (kind == TerminalFileKind.Keys && after.StartsWith(':') && Api.Commands.TryGetValue(word, out var command))
            return $"$(keyboard) **{word}**, {command.Title}" + (command.Key.Length > 0 ? $"\n\nOn {command.Key} unless this says otherwise." : "\n\nOn no key unless this says one.");

        return null;
    }

    /// <summary>What a call on the terminal object takes, for a signature under the caret, null for one it hasn't got.</summary>
    public string[]? Parameters(string call)
    {
        foreach (string name in ObjectNames)
        {
            if (call.StartsWith(name, StringComparison.Ordinal) && Api.Functions.TryGetValue(call[name.Length..], out var function))
                return function.Function.Parameters.Length == 0 ? [] : function.Function.Parameters.Split(", ");
        }

        return null;
    }

    /* What is wrong */

    private static readonly Regex KeyLine = new(@"^\s*""?([A-Za-z_][A-Za-z0-9_:]*)""?\s*:\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex Call = new(@"\b(terminal|dusk)\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>What is wrong with a terminal file, line by line, as warnings.</summary>
    public List<TerminalFileProblem> Check(TerminalFileKind kind, string text)
    {
        var found = new List<TerminalFileProblem>();
        string[] lines = text.Split('\n');
        int depth = 0;
        string? section = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            string code = WithoutComment(line);

            if (kind == TerminalFileKind.Plugin)
            {
                foreach (Match match in Call.Matches(code))
                {
                    string function = match.Groups[2].Value;
                    if (!Api.Functions.ContainsKey(function))
                        found.Add(new TerminalFileProblem(i, match.Groups[2].Index, function.Length, $"the terminal object has nothing called {function}"));
                }

                // A key a command asks for, the fourth argument of terminal.command
                int command = code.IndexOf(".command(", StringComparison.Ordinal);
                if (command >= 0 && LastText(code) is var (key, at) && key.Length > 0 && key.Contains('+') && TerminalShortcut.Read(key) is null)
                    found.Add(new TerminalFileProblem(i, at, key.Length, $"{key} isn't a key, they're written like Ctrl+Shift+K"));
                continue;
            }

            // The keys of the object a settings or keys file declares, one level in, or in the terminal's section
            bool settingLevel = kind == TerminalFileKind.Settings
                ? depth == 1 && section is null || depth == 2 && section == SettingsSections.TERMINAL
                : depth == 1;
            if (settingLevel && KeyLine.Match(code) is { Success: true } entry
                && !(kind == TerminalFileKind.Settings && depth == 1 && entry.Groups[1].Value is SettingsSections.TERMINAL or SettingsSections.EDITOR or SettingsSections.EXTENSIONS))
            {
                string key = entry.Groups[1].Value;
                int column = entry.Groups[1].Index;
                if (kind == TerminalFileKind.Settings)
                {
                    if (Api.Settings.Find(setting => setting.Key == key) is not { } setting)
                    {
                        found.Add(new TerminalFileProblem(i, column, key.Length, $"{key} is no setting, it is kept as it is and nothing reads it"));
                    }
                    else if (setting.Choices is { Length: > 0 } choices && FirstText(entry.Groups[2].Value) is { } chosen && !choices.Contains(chosen))
                    {
                        found.Add(new TerminalFileProblem(i, code.IndexOf('"' + chosen, StringComparison.Ordinal) + 1, chosen.Length, $"{key} is one of {string.Join(", ", choices)}"));
                    }
                }
                else if (kind == TerminalFileKind.Keys)
                {
                    if (Api.Commands.Count > 0 && !Api.Commands.ContainsKey(key))
                        found.Add(new TerminalFileProblem(i, column, key.Length, $"no plugin registers a command called {key}"));
                    if (FirstText(entry.Groups[2].Value) is { Length: > 0 } written && TerminalShortcut.Read(written) is null)
                        found.Add(new TerminalFileProblem(i, code.IndexOf('"' + written, StringComparison.Ordinal) + 1, written.Length, $"{written} isn't a key, they're written like Ctrl+Shift+K"));
                }
            }

            // A section of Hex's settings file (terminal, editor, extensions), the terminal's keys are one level further in
            if (kind == TerminalFileKind.Settings && depth == 1 && KeyLine.Match(code) is { Success: true } opened && code.Contains('{')
                && opened.Groups[1].Value is SettingsSections.TERMINAL or SettingsSections.EDITOR or SettingsSections.EXTENSIONS)
                section = opened.Groups[1].Value;

            int before = depth;
            depth += Count(code, '{') - Count(code, '}');

            // a section closed, back at the level of the sections
            if (before >= 2 && depth <= 1)
                section = null;
        }

        return found;
    }

    private static int Count(string text, char character)
    {
        int count = 0;
        bool quoted = false;
        foreach (char each in text)
        {
            if (each == '"') quoted = !quoted;
            else if (each == character && !quoted) count++;
        }

        return count;
    }

    private static string WithoutComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (!quoted && line[i] == '/' && line[i + 1] == '/') return line[..i];
        }

        return line;
    }

    private static string? FirstText(string text)
    {
        int open = text.IndexOf('"');
        int close = open < 0 ? -1 : text.IndexOf('"', open + 1);
        return close > open ? text[(open + 1)..close] : null;
    }

    private static (string Text, int At)? LastText(string line)
    {
        int close = line.LastIndexOf('"');
        int open = close <= 0 ? -1 : line.LastIndexOf('"', close - 1);
        return open >= 0 ? (line[(open + 1)..close], open + 1) : null;
    }
}
