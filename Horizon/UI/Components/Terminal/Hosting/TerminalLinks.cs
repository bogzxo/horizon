using System.Text.RegularExpressions;

namespace Horizon.UI.Components;

/// <summary>A link found in a terminal's text, a URL or a file with the line and column after it, and the cells it covers.</summary>
public readonly record struct TerminalLink(string Target, int From, int To, string? File = null, int Line = 0, int Column = 0)
{
    public bool IsUrl => File is null;
}

/// <summary>
/// The links in a line of a terminal's text, the ones a program didn't mark with OSC 8. A URL, and a file with a line
/// after it the ways compilers and runtimes write one: <c>Program.cs:42</c>, <c>Program.cs:42:7</c>,
/// <c>Program.cs(42,7)</c>, <c>File "x.py", line 42</c> and <c>in C:\...\x.cs:line 42</c>, and a path that is plainly a
/// path (it has a slash and a dot) on its own. A file is only taken for a link when it is there, from the folder the
/// shell is in, so a word that looks like one isn't underlined for nothing.
/// </summary>
public static class TerminalLinks
{
    private static readonly Regex Url = new(@"\b(?:https?|file)://[^\s""'<>()\[\]{}]+[^\s""'<>()\[\]{}.,;:!?]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The ways a place in a file is written, the file, then the line, then maybe the column
    private static readonly Regex[] Places =
    [
        new(@"File ""(?<file>[^""]+)"", line (?<line>\d+)", RegexOptions.Compiled),
        new(@"in (?<file>(?:[A-Za-z]:)?[\\/][^:\r\n]+?):line (?<line>\d+)", RegexOptions.Compiled),
        new(@"(?<file>(?:[A-Za-z]:)?[\w.\-\\/~]+\.[A-Za-z0-9]+)\((?<line>\d+)(?:,(?<column>\d+))?\)", RegexOptions.Compiled),
        new(@"(?<file>(?:[A-Za-z]:)?[\w.\-\\/~]+\.[A-Za-z0-9]+):(?<line>\d+)(?::(?<column>\d+))?", RegexOptions.Compiled),
        new(@"(?<file>(?:[A-Za-z]:|\.{1,2}|~)?[\\/][\w.\-\\/]+\.[A-Za-z0-9]+)", RegexOptions.Compiled)
    ];

    /// <summary>Every link on a line, left to right, files only when they are there from the folder given.</summary>
    public static List<TerminalLink> Find(string line, string? folder, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var found = new List<TerminalLink>();
        bool Taken(int from, int to) => found.Exists(link => from < link.To && to > link.From);

        foreach (Match match in Url.Matches(line))
            found.Add(new TerminalLink(match.Value, match.Index, match.Index + match.Length));

        foreach (Regex place in Places)
        {
            foreach (Match match in place.Matches(line))
            {
                if (Taken(match.Index, match.Index + match.Length))
                    continue;

                string file = match.Groups["file"].Value;
                string? full = Resolve(file, folder, exists);
                if (full is null)
                    continue;

                int number = match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : 0;
                int column = match.Groups["column"].Success ? int.Parse(match.Groups["column"].Value) : 0;
                found.Add(new TerminalLink(full, match.Index, match.Index + match.Length, full, number, column));
            }
        }

        found.Sort((a, b) => a.From.CompareTo(b.From));
        return found;
    }

    /// <summary>The link on a line that covers a column, null for none.</summary>
    public static TerminalLink? At(string line, int column, string? folder, Func<string, bool>? exists = null)
    {
        foreach (TerminalLink link in Find(line, folder, exists))
        {
            if (column >= link.From && column < link.To)
                return link;
        }

        return null;
    }

    /// <summary>A file written in a terminal as the file it is, from the folder the shell is in, null when it isn't there.</summary>
    public static string? Resolve(string file, string? folder, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (file.StartsWith('~'))
            file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file[1..].TrimStart('/', '\\'));

        try
        {
            string full = Path.IsPathRooted(file) || folder is null ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(folder, file));
            return exists(full) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
