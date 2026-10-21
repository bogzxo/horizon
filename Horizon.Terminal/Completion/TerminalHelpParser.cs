using System.Text.RegularExpressions;

namespace Horizon.UI.Components;

/// <summary>One thing a command takes, a subcommand or a flag, and the bit of help that came with it.</summary>
public readonly record struct TerminalHelpItem(string Name, string? Note);

/// <summary>What a command's help said it takes, its subcommands and its flags, in the order the help had them.</summary>
public sealed class TerminalHelpTopic
{
    public static readonly TerminalHelpTopic Empty = new([], []);

    public IReadOnlyList<TerminalHelpItem> Subcommands { get; }
    public IReadOnlyList<TerminalHelpItem> Flags { get; }

    public TerminalHelpTopic(IReadOnlyList<TerminalHelpItem> subcommands, IReadOnlyList<TerminalHelpItem> flags)
    {
        Subcommands = subcommands;
        Flags = flags;
    }

    /// <summary>Whether a word is one of its subcommands.</summary>
    public bool HasSubcommand(string word)
    {
        foreach (TerminalHelpItem item in Subcommands)
        {
            if (item.Name.Equals(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Reads what a command printed for --help into its subcommands and flags, so a CLI nobody wrote completion for gets
/// some anyway. Help has no format, but most of it looks alike. A flag starts a line with a dash and its help comes
/// after two spaces or more. A subcommand is an indented word under a heading with "command" in it (git, dotnet, docker,
/// cargo, gh, kubectl), a comma list under one (npm), or the word after the command in its usage lines
/// ("usage: git stash list", "or: git stash pop"). Whatever doesn't look like any of that is left alone.
/// </summary>
public static partial class TerminalHelpParser
{
    // Help over this is cut down to it, a note is a hint and not the manual
    private const int MOST_NOTE = 60;

    [GeneratedRegex(@"(?<![\w-])(--?\[no-\][A-Za-z0-9][\w-]*|--?[A-Za-z0-9?][\w.-]*)")]
    private static partial Regex FlagPattern();

    [GeneratedRegex(@"^[A-Za-z][\w:.-]*$")]
    private static partial Regex WordPattern();

    /// <param name="help">What the command printed.</param>
    /// <param name="command">The command and the subcommands that were asked about ("git stash"), for its usage lines.</param>
    public static TerminalHelpTopic Parse(string help, string command)
    {
        var subcommands = new List<TerminalHelpItem>();
        var flags = new List<TerminalHelpItem>();
        var seenSub = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFlag = new HashSet<string>(StringComparer.Ordinal);
        string[] commandWords = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        bool inCommands = false;
        // How far in a section's subcommands are, its first one says. A line further in is the help of the one above
        // wrapped onto another line, and a word on its own there isn't a subcommand
        int commandIndent = -1;
        foreach (string raw in help.Replace("\r", string.Empty).Replace("\t", "    ").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            bool indented = char.IsWhiteSpace(line[0]);
            string trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;

            // A heading, the commands start under one that says so and stop at the next that doesn't
            if (!indented && !trimmed.StartsWith('-'))
            {
                if (UsageSubcommand(trimmed, commandWords) is { } used && seenSub.Add(used))
                    subcommands.Add(new TerminalHelpItem(used, null));
                inCommands = trimmed.Contains("command", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("usage", StringComparison.OrdinalIgnoreCase);
                commandIndent = -1;
                continue;
            }

            if (trimmed.StartsWith('-'))
            {
                ReadFlags(trimmed, flags, seenFlag);
                continue;
            }

            // "or: git stash pop" under the first usage line
            if (UsageSubcommand(trimmed, commandWords) is { } usage)
            {
                if (seenSub.Add(usage))
                    subcommands.Add(new TerminalHelpItem(usage, null));
                continue;
            }

            if (inCommands && indented)
            {
                if (commandIndent < 0)
                    commandIndent = indent;
                if (indent == commandIndent)
                    ReadSubcommands(trimmed, subcommands, seenSub);
            }
        }

        return new TerminalHelpTopic(subcommands, flags);
    }

    // The word after the command in a usage line, "usage: git stash list" or "or: git stash pop", null for none
    private static string? UsageSubcommand(string line, string[] commandWords)
    {
        int colon = line.IndexOf(':');
        if (colon < 0 || colon > 8)
            return null;

        string lead = line[..colon].Trim();
        if (!lead.Equals("usage", StringComparison.OrdinalIgnoreCase) && !lead.Equals("or", StringComparison.OrdinalIgnoreCase))
            return null;

        string[] words = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= commandWords.Length)
            return null;

        for (int i = 0; i < commandWords.Length; i++)
        {
            // The program's name as it was run can come with a path or an extension
            string said = Path.GetFileNameWithoutExtension(words[i]);
            if (!said.Equals(commandWords[i], StringComparison.OrdinalIgnoreCase))
                return null;
        }

        string next = words[commandWords.Length];
        return WordPattern().IsMatch(next) ? next : null;
    }

    private static void ReadFlags(string line, List<TerminalHelpItem> flags, HashSet<string> seen)
    {
        // The flags are what comes before a gap of two spaces, the help after it
        int gap = line.IndexOf("  ", StringComparison.Ordinal);
        string names = gap < 0 ? line : line[..gap];
        string? note = gap < 0 ? null : Note(line[gap..]);

        foreach (Match match in FlagPattern().Matches(names))
        {
            string flag = match.Value;
            if (flag.Length < 2 || flag == "--")
                continue;

            // --[no-]verify is two flags
            if (flag.Contains("[no-]", StringComparison.Ordinal))
            {
                Add(flag.Replace("[no-]", string.Empty, StringComparison.Ordinal));
                Add(flag.Replace("[no-]", "no-", StringComparison.Ordinal));
                continue;
            }

            Add(flag);
        }

        void Add(string flag)
        {
            // A flag that takes a value is offered with its = so the value goes straight after
            int at = names.IndexOf(flag, StringComparison.Ordinal);
            bool takesValue = at >= 0 && at + flag.Length < names.Length && names[at + flag.Length] == '=';
            string offered = takesValue ? flag + "=" : flag;
            if (seen.Add(offered))
                flags.Add(new TerminalHelpItem(offered, note));
        }
    }

    private static void ReadSubcommands(string line, List<TerminalHelpItem> subcommands, HashSet<string> seen)
    {
        // npm's "access, adduser, audit, bugs," a line of them
        if (line.Contains(", ", StringComparison.Ordinal) && !line.Contains("  ", StringComparison.Ordinal))
        {
            foreach (string part in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (WordPattern().IsMatch(part) && seen.Add(part))
                    subcommands.Add(new TerminalHelpItem(part, null));
            }
            return;
        }

        // "  commit    Record changes to the repository", the word and its help two spaces or more apart
        int gap = line.IndexOf("  ", StringComparison.Ordinal);
        string name = (gap < 0 ? line : line[..gap]).Trim();

        // "build, b    Compile the package" names two ways, the first is the one offered, and gh puts a colon after it
        int comma = name.IndexOf(',');
        if (comma > 0)
            name = name[..comma];
        name = name.TrimEnd(':');

        if (gap < 0 && name.Contains(' '))
            return;
        if (!WordPattern().IsMatch(name) || !seen.Add(name))
            return;

        string? note = gap < 0 ? null : Note(line[gap..]);
        // an alias written as "co = checkout" says what it is
        if (note is not null && note.StartsWith('='))
            note = "alias for " + note[1..].Trim();
        subcommands.Add(new TerminalHelpItem(name, note));
    }

    private static string? Note(string text)
    {
        string note = text.Trim();
        if (note.Length == 0)
            return null;
        return note.Length <= MOST_NOTE ? note : note[..(MOST_NOTE - 1)].TrimEnd() + "…";
    }
}
