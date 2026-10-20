namespace Horizon.UI.Components;

/// <summary>What could finish the word being typed at a prompt, for the list a terminal shows under it (<see cref="TerminalView.Completer"/>).</summary>
public interface ITerminalCompleter
{
    /// <summary>
    /// What fits the word that ends the line, best first, and where that word starts. Empty when nothing does or the
    /// word isn't one to finish.
    /// </summary>
    IReadOnlyList<TerminalLineCompletion> Complete(string line, string? folder, out int wordStart);
}

/// <summary>One thing a word could be finished as, the whole of it and whether it is a folder (it gets its separator on).</summary>
public readonly record struct TerminalLineCompletion(string Text, bool Folder, bool Command);

/// <summary>
/// cmd's and bash's completion, the commands the shell has for the first word of a command and the files and folders
/// of where a path is being typed anywhere. PowerShell and fish do their own and better, they don't get this.
/// </summary>
public sealed class TerminalShellCompletion : ITerminalCompleter
{
    public const int MOST = 8;

    private readonly TerminalShellLine line;
    private readonly char separator;

    public TerminalShellCompletion(TerminalShellLine line)
    {
        this.line = line;
        separator = line.Kind == "cmd" ? '\\' : '/';
    }

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
