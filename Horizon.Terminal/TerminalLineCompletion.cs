namespace Horizon.UI.Components;

/// <summary>What could finish the word being typed at a prompt, for the list a terminal shows under it (<see cref="TerminalView.Completer"/>).</summary>
public interface ITerminalCompleter
{
    /// <summary>
    /// What fits the word that ends the line, best first, and where that word starts. Empty when nothing does or the
    /// word isn't one to finish.
    /// </summary>
    IReadOnlyList<TerminalLineCompletion> Complete(string line, string? folder, out int wordStart);

    /// <summary>Goes up when the completer learned something that would change a list (a tool's help came in), the list is worked out again.</summary>
    int Version => 0;
}

/// <summary>
/// One thing a word could be finished as, the whole of it and whether it is a folder (it gets its separator on), and
/// a bit of help shown dim beside it, a subcommand's or a flag's.
/// </summary>
public readonly record struct TerminalLineCompletion(string Text, bool Folder, bool Command, string? Note = null);

/// <summary>
/// cmd's and bash's completion, the commands the shell has for the first word of a command and the files and folders
/// of where a path is being typed anywhere, and for every shell the words after a known tool (git's subcommands, its
/// branches, dotnet's flags, see <see cref="TerminalToolCompletion"/>). PowerShell does commands and files itself and
/// better, so it only gets the tools.
/// </summary>
public sealed class TerminalShellCompletion : ITerminalCompleter
{
    public const int MOST = 8;

    private readonly TerminalShellLine line;
    private readonly TerminalToolCompletion? tools;
    private readonly bool toolsOnly;
    private readonly char separator;

    /// <param name="tools">What completes the words after a tool, null for none (a WSL shell, whose tools aren't this machine's).</param>
    /// <param name="toolsOnly">Whether the commands and files are the shell's own business, PowerShell's.</param>
    public TerminalShellCompletion(TerminalShellLine line, TerminalToolCompletion? tools = null, bool toolsOnly = false)
    {
        this.line = line;
        this.tools = tools;
        this.toolsOnly = toolsOnly;
        separator = line.Kind == "cmd" ? '\\' : '/';
    }

    public int Version => tools?.Version ?? 0;

    public IReadOnlyList<TerminalLineCompletion> Complete(string typed, string? folder, out int wordStart)
    {
        wordStart = typed.Length;
        if (typed.Length == 0 || char.IsWhiteSpace(typed[^1]))
            return [];

        // The word being typed, back to a space or an operator
        int start = typed.Length;
        while (start > 0 && !char.IsWhiteSpace(typed[start - 1]) && typed[start - 1] is not ('|' or '&' or ';' or '<' or '>' or '(' or '"'))
            start--;
        string word = typed[start..];
        wordStart = start;

        // The first word of a command, with nothing before it but operators
        int before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(typed[before]))
            before--;
        bool first = before < 0 || typed[before] is '|' or '&' or ';' or '(';

        // After a tool on the list its own words come first, the files after them
        if (!first && tools?.Complete(CommandWords(typed, start), word, folder) is { } fromTool)
        {
            if (!toolsOnly && !word.StartsWith('-'))
                AddFiles(word, folder, fromTool);
            if (fromTool.Count > MOST)
                fromTool.RemoveRange(MOST, fromTool.Count - MOST);
            return fromTool;
        }

        if (toolsOnly)
            return [];

        var found = new List<TerminalLineCompletion>();
        bool pathLike = word.IndexOfAny(['/', '\\']) >= 0 || word.StartsWith('.') || word.StartsWith('~');
        if (first && !pathLike)
        {
            foreach (string name in line.Lookup.StartingWith(word))
            {
                // git and git.exe are one command to cmd, the one without the extension is offered
                if (name.Equals(word, StringComparison.OrdinalIgnoreCase) || line.Kind == "cmd" && name.Contains('.') && !word.Contains('.'))
                    continue;
                found.Add(new TerminalLineCompletion(name, false, true));
                if (found.Count >= MOST * 4)
                    break;
            }
        }

        AddFiles(word, folder, found);
        found.Sort(static (a, b) => a.Text.Length != b.Text.Length ? a.Text.Length - b.Text.Length : string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));
        if (found.Count > MOST)
            found.RemoveRange(MOST, found.Count - MOST);
        return found;
    }

    /// <summary>
    /// Helper method to split the command the word is in into its words, back to the last operator, quotes kept
    /// together and taken off. A word in quotes isn't a subcommand anyway.
    /// </summary>
    public static List<string> CommandWords(string typed, int end)
    {
        int from = end;
        while (from > 0 && typed[from - 1] is not ('|' or '&' or ';' or '('))
            from--;

        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        char quote = '\0';
        for (int i = from; i < end; i++)
        {
            char c = typed[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
            }
            else
            {
                word.Append(c);
            }
        }

        if (word.Length > 0)
            words.Add(word.ToString());
        return words;
    }

    private void AddFiles(string word, string? folder, List<TerminalLineCompletion> found)
    {
        int cut = word.LastIndexOfAny(['/', '\\']);
        string folderPart = cut < 0 ? string.Empty : word[..(cut + 1)], namePart = word[(cut + 1)..];
        string? where = folderPart.Length == 0 ? folder : line.Resolve(folderPart, folder);
        if (where is null || !Directory.Exists(where) || namePart.Length == 0 && folderPart.Length == 0)
            return;

        try
        {
            int seen = 0;
            foreach (string entry in Directory.EnumerateFileSystemEntries(where, namePart + "*"))
            {
                string name = Path.GetFileName(entry);
                if (name.Equals(namePart, StringComparison.OrdinalIgnoreCase) || name.StartsWith('.') && !namePart.StartsWith('.'))
                    continue;
                bool isFolder = Directory.Exists(entry);
                found.Add(new TerminalLineCompletion(folderPart + name + (isFolder ? separator.ToString() : string.Empty), isFolder, false));
                if (++seen >= MOST * 4)
                    break;
            }
        }
        catch (Exception)
        {
        }
    }
}
