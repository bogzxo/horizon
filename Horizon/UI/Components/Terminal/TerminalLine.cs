namespace Horizon.UI.Components;

/// <summary>What a character of the line typed at a prompt is, for its colour.</summary>
public enum TerminalLineKind : byte
{
    None,

    /// <summary>The first word of a command and the shell knows it, green.</summary>
    Command,

    /// <summary>The first word of a command and the shell doesn't know it, red, fish's trick.</summary>
    MissingCommand,

    /// <summary>A shell's own word that takes a command after it (if, sudo, do), coloured as a command.</summary>
    Keyword,

    Flag,
    String,

    /// <summary>A string that hasn't been closed yet, dimmed to say so.</summary>
    OpenString,

    Variable,
    Number,
    Operator,
    Comment,

    /// <summary>A word that is a file that is there, underlined.</summary>
    Path,

    /// <summary>A word that is a folder that is there, underlined in the folder's colour.</summary>
    Folder
}

/// <summary>Colours a line typed at a prompt, a kind a character, for <see cref="TerminalView.LineColourer"/>.</summary>
public interface ITerminalLineColourer
{
    /// <param name="line">What is typed, from the end of the prompt, a character a cell.</param>
    /// <param name="kinds">As long as the line, written with what each character is.</param>
    /// <param name="folder">The folder the shell is in as far as anybody knows, for the paths.</param>
    void Colour(ReadOnlySpan<char> line, Span<TerminalLineKind> kinds, string? folder);

    /// <summary>Goes up when the same line would come out coloured differently (a shell said what commands it has), asked every tick.</summary>
    int Version { get; }
}

/// <summary>
/// The colourer of the shells' lines, PowerShell, bash and cmd, read with a small lexer of its own (the shells'
/// grammars have far more in them than a line at a prompt needs, and this makes no garbage but the paths it checks).
/// The first word of every command of the line is looked up (<see cref="TerminalCommandLookup"/>), green when the
/// shell has it and red when it hasn't, left alone while that isn't known for sure (a WSL shell before it has said
/// what it has). Flags, strings, variables, numbers, operators and comments by their look, and a word that is a file
/// or a folder that is there underlined.
/// </summary>
public sealed class TerminalShellLine : ITerminalLineColourer
{
    /// <summary>"powershell", "bash", "cmd", or anything else for a shell read like bash.</summary>
    public string Kind { get; }

    public TerminalCommandLookup Lookup { get; }

    private readonly bool cmd, powershell;

    public TerminalShellLine(string kind, TerminalCommandLookup lookup)
    {
        Kind = kind;
        Lookup = lookup;
        cmd = kind == "cmd";
        powershell = kind == "powershell";
    }

    public int Version => Lookup.Generation;

    public void Colour(ReadOnlySpan<char> line, Span<TerminalLineKind> kinds, string? folder)
    {
        kinds[..line.Length].Clear();
        bool command = true, redirect = false;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // A comment runs to the end, # where a word starts (rem for cmd)
            if (c == '#' && !cmd)
            {
                kinds[i..line.Length].Fill(TerminalLineKind.Comment);
                return;
            }

            if (IsOperator(c))
            {
                int start = i;
                while (i < line.Length && IsOperator(line[i]))
                    i++;
                kinds[start..i].Fill(TerminalLineKind.Operator);
                ReadOnlySpan<char> op = line[start..i];
                redirect = op.Contains('>') || op.Contains('<');
                // a pipe, a ; or a && starts another command, a redirection is followed by a file
                if (!redirect)
                    command = true;
                continue;
            }

            if (c == '"' || c == '\'' && !cmd)
            {
                int start = i;
                i++;
                while (i < line.Length && line[i] != c)
                {
                    // a backtick in PowerShell and a backslash in bash's double quotes keep the next one in
                    if ((powershell && line[i] == '`' || !powershell && !cmd && c == '"' && line[i] == '\\') && i + 1 < line.Length)
                        i++;
                    i++;
                }

                bool closed = i < line.Length;
                if (closed) i++;
                kinds[start..i].Fill(closed ? TerminalLineKind.String : TerminalLineKind.OpenString);
                command = false;
                redirect = false;
                continue;
            }

            if (c == '$' && !cmd)
            {
                int start = i;
                i++;
                if (i < line.Length && line[i] == '{')
                {
                    while (i < line.Length && line[i] != '}') i++;
                    if (i < line.Length) i++;
                }
                else
                {
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] is '_' or ':' or '?'))
                        i++;
                }

                kinds[start..i].Fill(TerminalLineKind.Variable);
                command = false;
                continue;
            }

            // A word, to the next space or operator
            int from = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]) && !IsOperator(line[i]) && !(line[i] is '"' && i > from && cmd))
                i++;
            ReadOnlySpan<char> word = line[from..i];

            if (cmd && command && word.Equals("rem", StringComparison.OrdinalIgnoreCase))
            {
                kinds[from..line.Length].Fill(TerminalLineKind.Comment);
                return;
            }

            if (cmd && word.Length > 2 && word[0] == '%' && word[^1] == '%')
            {
                kinds[from..i].Fill(TerminalLineKind.Variable);
                command = false;
                continue;
            }

            if (command && !redirect)
            {
                // bash's NAME=value before a command is a variable and the command is still to come
                if (!cmd && !powershell && word.IndexOf('=') > 0 && IsName(word[..word.IndexOf('=')]))
                {
                    kinds[from..i].Fill(TerminalLineKind.Variable);
                    continue;
                }

                if (Lookup.IsKeyword(word))
                {
                    kinds[from..i].Fill(TerminalLineKind.Keyword);
                    continue;
                }

                TerminalLineKind kind = CommandKind(word, folder);
                kinds[from..i].Fill(kind);
                command = false;
                continue;
            }

            redirect = false;
            if (IsFlag(word))
                kinds[from..i].Fill(TerminalLineKind.Flag);
            else if (IsNumber(word))
                kinds[from..i].Fill(TerminalLineKind.Number);
            else if (PathKind(word, folder) is var path and not TerminalLineKind.None)
                kinds[from..i].Fill(path);
        }
    }

    private TerminalLineKind CommandKind(ReadOnlySpan<char> word, string? folder)
    {
        // A path to a program is there or it isn't
        if (word.IndexOfAny('/', '\\') >= 0)
        {
            string? full = Resolve(word, folder);
            if (full is null)
                return TerminalLineKind.None;
            if (File.Exists(full) || cmd && File.Exists(full + ".exe") || powershell && File.Exists(full + ".ps1"))
                return TerminalLineKind.Command;
            return TerminalLineKind.MissingCommand;
        }

        return Lookup.Knows(word) switch
        {
            true => TerminalLineKind.Command,
            false => TerminalLineKind.MissingCommand,
            _ => TerminalLineKind.None
        };
    }

    private bool IsFlag(ReadOnlySpan<char> word)
    {
        if (word.Length > 1 && word[0] == '-' && !IsNumber(word))
            return true;
        // cmd's switches, /s and /b, short so as not to take a path
        return cmd && word.Length is >= 2 and <= 4 && word[0] == '/' && char.IsLetterOrDigit(word[1]);
    }

    private static bool IsNumber(ReadOnlySpan<char> word)
    {
        int at = word.Length > 1 && word[0] == '-' ? 1 : 0;
        if (at >= word.Length || !char.IsAsciiDigit(word[at]))
            return false;
        for (int i = at; i < word.Length; i++)
        {
            if (!char.IsAsciiDigit(word[i]) && word[i] is not '.' and not '_')
                return false;
        }

        return true;
    }

    private static bool IsName(ReadOnlySpan<char> word)
    {
        if (word.IsEmpty || char.IsAsciiDigit(word[0]))
            return false;
        foreach (char c in word)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private bool IsOperator(char c) => c switch
    {
        '|' or ';' or '<' or '>' => true,
        '&' => true,
        '(' or ')' => !cmd,
        '{' or '}' => powershell,
        _ => false
    };

    private TerminalLineKind PathKind(ReadOnlySpan<char> word, string? folder)
    {
        // a bare word is checked too, a file or folder of the folder the shell is in is most of what follows cd and cat
        if (word.Length > 260 || word.IndexOfAny('*', '?') >= 0)
            return TerminalLineKind.None;

        string? full = Resolve(word, folder);
        if (full is null)
            return TerminalLineKind.None;
        if (Directory.Exists(full))
            return TerminalLineKind.Folder;
        return File.Exists(full) ? TerminalLineKind.Path : TerminalLineKind.None;
    }

    /// <summary>A word as the shell would read it as a path, ~ and the variables expanded, against the folder it is in.</summary>
    public string? Resolve(ReadOnlySpan<char> word, string? folder)
    {
        try
        {
            string path = word.Trim('"').ToString();
            if (path.StartsWith('~'))
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
            if (path.Contains('%'))
                path = Environment.ExpandEnvironmentVariables(path);
            if (path.StartsWith("$env:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("$HOME", StringComparison.Ordinal))
            {
                int end = path.IndexOfAny(['/', '\\'], 1);
                string name = path[(path.StartsWith("$env:", StringComparison.OrdinalIgnoreCase) ? 5 : 1)..(end < 0 ? path.Length : end)];
                if (Environment.GetEnvironmentVariable(name) is not { } value)
                    return null;
                path = value + (end < 0 ? string.Empty : path[end..]);
            }

            // bash on Windows writes /c/Users, which is C:\Users
            if (OperatingSystem.IsWindows() && path.Length >= 2 && path[0] == '/' && char.IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/'))
                path = char.ToUpperInvariant(path[1]) + ":" + (path.Length == 2 ? "\\" : path[2..]);

            if (!Path.IsPathRooted(path))
            {
                if (folder is null)
                    return null;
                path = Path.Combine(folder, path);
            }

            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
