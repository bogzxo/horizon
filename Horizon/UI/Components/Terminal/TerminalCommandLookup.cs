namespace Horizon.UI.Components;

/// <summary>
/// Whether a shell has a command, for the colour of the first word of a line. Three places it can be. The shell's own
/// words (cd, echo, set), written down here a shell at a time. The programs of the PATH, scanned once on a worker and
/// kept for every terminal. And a file the shell wrote itself as it started, every alias, function and cmdlet of a
/// PowerShell (`Get-Command`, when it is first idle) and everything `compgen -c` knows in a bash, named by
/// DUSK_COMMANDS_FILE, which the integration scripts write and this reads again whenever a watcher says it changed
/// (nothing on disk is looked at from the paint or the update, a stat twice a second was garbage in every tick). A shell this
/// can't be sure about (a bash before it has written its file, the PATH of a WSL machine is not Windows') gets no
/// answer rather than a red word that's wrong.
/// </summary>
public sealed class TerminalCommandLookup
{
    private static readonly StringComparer Names = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly string[] CmdWords =
    [
        "assoc", "break", "call", "cd", "chdir", "cls", "color", "copy", "date", "del", "dir", "echo", "endlocal", "erase",
        "exit", "for", "ftype", "goto", "if", "md", "mkdir", "mklink", "move", "path", "pause", "popd", "prompt", "pushd",
        "rd", "rem", "ren", "rename", "rmdir", "set", "setlocal", "shift", "start", "time", "title", "type", "ver",
        "verify", "vol"
    ];

    private static readonly string[] BashWords =
    [
        "alias", "bg", "bind", "break", "builtin", "caller", "cd", "command", "compgen", "complete", "continue", "declare",
        "dirs", "disown", "echo", "enable", "eval", "exec", "exit", "export", "false", "fc", "fg", "getopts", "hash",
        "help", "history", "jobs", "kill", "let", "local", "logout", "mapfile", "popd", "printf", "pushd", "pwd", "read",
        "readarray", "readonly", "return", "set", "shift", "shopt", "source", ".", "suspend", "test", "[", "[[", "times",
        "trap", "true", "type", "typeset", "ulimit", "umask", "unalias", "unset", "wait", "case", "esac", "fi", "done",
        "for", "function", "select", "in"
    ];

    // PowerShell's own aliases and words, there whatever the profile did, so they are green before Get-Command answers
    private static readonly string[] PowerShellWords =
    [
        "cd", "chdir", "ls", "dir", "gci", "cat", "gc", "type", "echo", "write", "cls", "clear", "cp", "copy", "cpi", "mv",
        "move", "mi", "rm", "del", "erase", "ri", "rd", "rmdir", "mkdir", "md", "ni", "pwd", "gl", "sl", "pushd", "popd",
        "ii", "iwr", "irm", "curl", "wget", "sort", "where", "?", "select", "foreach", "%", "measure", "man", "help",
        "history", "h", "r", "kill", "ps", "gps", "sleep", "start", "saps", "tee", "fl", "ft", "gm", "gal", "set", "sv",
        "gv", "exit", "return", "function", "filter", "param", "try", "catch", "finally", "throw", "break", "continue",
        "switch", "foreach", "for", "while", "do", "if", "elseif", "else", "trap", "begin", "process", "end", "class",
        "enum", "using", "ipmo", "iex", "icm", "cls", "rvpa", "gi", "gp", "sp", "rni", "ren", "diff", "compare", "group",
        "Get-ChildItem", "Get-Content", "Set-Location", "Get-Location", "Write-Output", "Write-Host", "Get-Command",
        "Get-Help", "Get-Process", "Stop-Process", "Start-Process", "Remove-Item", "Copy-Item", "Move-Item", "New-Item",
        "Select-Object", "Where-Object", "ForEach-Object", "Sort-Object", "Measure-Object", "Invoke-WebRequest",
        "Invoke-RestMethod", "Import-Module", "Get-Item", "Set-Content", "Add-Content", "Out-File", "Clear-Host",
        "Test-Path", "Join-Path", "Split-Path", "Resolve-Path", "Get-Date", "Start-Sleep", "Format-List", "Format-Table"
    ];

    // The words after which the next word is a command again
    private static readonly string[] BashKeywords = ["if", "then", "else", "elif", "do", "while", "until", "sudo", "time", "exec", "nohup", "command", "builtin", "!", "xargs", "env", "watch"];
    private static readonly string[] CmdKeywords = ["call", "start"];

    private static volatile HashSet<string>? pathCommands;
    private static int pathStarted;

    /// <summary>"powershell", "bash", "cmd", or what else a profile is.</summary>
    public string Kind { get; }

    /// <summary>The file the shell writes what it has into, null for none.</summary>
    public string? File { get; }

    private readonly HashSet<string> words, keywords;
    private HashSet<string>.AlternateLookup<ReadOnlySpan<char>> wordLookup, keywordLookup;
    private volatile HashSet<string>? fileCommands;
    private readonly HashSet<string>? given;
    private readonly bool givenSure;
    private int generation;
    private FileSystemWatcher? watcher;

    /// <summary>Goes up whenever it has learned something, the PATH scanned or the shell's file read, so a line is looked up again.</summary>
    public int Generation => Volatile.Read(ref generation) + pathGeneration;

    private static volatile int pathGeneration;
    private int reading;

    /// <param name="kind">Which shell, see <see cref="TerminalIntegration.KindOf"/>.</param>
    /// <param name="file">The file the shell writes its commands into, null for none.</param>
    public TerminalCommandLookup(string kind, string? file = null)
    {
        Kind = kind;
        File = file;
        words = new HashSet<string>(kind switch { "cmd" => CmdWords, "powershell" => PowerShellWords, "bash" => BashWords, _ => [] }, Names);
        keywords = new HashSet<string>(kind switch { "cmd" => CmdKeywords, "powershell" => [], _ => BashKeywords }, Names);
        wordLookup = words.GetAlternateLookup<ReadOnlySpan<char>>();
        keywordLookup = keywords.GetAlternateLookup<ReadOnlySpan<char>>();
        if (kind is "cmd" or "powershell")
            ScanPath();
        if (file is not null)
            Watch(file);
    }

    /// <summary>A lookup told what there is, for a test, sure or not about what isn't in it.</summary>
    public TerminalCommandLookup(string kind, IEnumerable<string> known, bool sure) : this(kind, null)
    {
        given = new HashSet<string>(known, Names);
        givenSure = sure;
    }

    /// <summary>Whether the shell has the command, null while that can't be known.</summary>
    public bool? Knows(ReadOnlySpan<char> word)
    {
        if (word.IsEmpty)
            return null;
        if (wordLookup.Contains(word))
            return true;

        if (given is not null)
            return given.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word) ? true : givenSure ? false : null;

        HashSet<string>? written = fileCommands;
        if (written is not null && written.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word))
            return true;

        HashSet<string>? path = pathCommands;
        bool usesPath = Kind is "cmd" or "powershell";
        if (usesPath && path is not null && path.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word))
            return true;

        // Sure once everything it could be in has been read
        return Kind switch
        {
            "cmd" => path is null ? null : false,
            "powershell" => path is null || written is null ? null : false,
            _ => written is null ? null : false
        };
    }

    /// <summary>Whether a word is one after which comes a command (sudo, if, do).</summary>
    public bool IsKeyword(ReadOnlySpan<char> word) => keywordLookup.Contains(word);

    /// <summary>The commands it knows that start with what is typed, for a completion, the shell's own words first.</summary>
    public IEnumerable<string> StartingWith(string start)
    {
        var seen = new HashSet<string>(Names);
        IEnumerable<string> all = words.Concat(given ?? []).Concat(fileCommands ?? []).Concat(Kind is "cmd" or "powershell" ? pathCommands ?? [] : []);
        foreach (string name in all)
        {
            if (name.StartsWith(start, StringComparison.OrdinalIgnoreCase) && seen.Add(name))
                yield return name;
        }
    }

    // The file read now if it is there, and again on a worker whenever the watcher says it was written
    private void Watch(string file)
    {
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
            Directory.CreateDirectory(folder);
            watcher = new FileSystemWatcher(folder, Path.GetFileName(file))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            watcher.Changed += (_, _) => ReadSoon(file);
            watcher.Created += (_, _) => ReadSoon(file);
            watcher.Renamed += (_, _) => ReadSoon(file);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // no watching, what is there now is all it knows
        }

        if (System.IO.File.Exists(file))
            ReadSoon(file);
    }

    // a moment after the last write, the shell writes it in more than one go
    private void ReadSoon(string file)
    {
        if (Interlocked.Exchange(ref reading, 1) == 1)
            return;
        Task.Run(async () =>
        {
            await Task.Delay(150);
            ReadFile(file);
        });
    }

    private void ReadFile(string file)
    {
        try
        {
            var read = new HashSet<string>(Names);
            foreach (string line in System.IO.File.ReadLines(file))
            {
                string name = line.Trim();
                if (name.Length > 0)
                    read.Add(name);
            }

            fileCommands = read;
            Interlocked.Increment(ref generation);
        }
        catch (Exception)
        {
        }
        finally
        {
            Volatile.Write(ref reading, 0);
        }
    }

    /// <summary>Waits for the PATH and the shell's file to have been read, for a test.</summary>
    public bool WaitUntilSure(int milliseconds)
    {
        long until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            if (Knows("\u0001definitely-not-a-command") is not null)
                return true;
            Thread.Sleep(20);
        }

        return false;
    }

    // The PATH the programs were last scanned out of, scanned again when a new terminal finds it changed
    private static volatile string? pathScanned;

    /// <summary>
    /// The PATH's programs, scanned on a worker for every terminal there is, and again when a terminal is opened after the
    /// PATH changed (it's read as the system has it now, see ProcessEnvironment, a program added to the PATH after Dusk
    /// started was coloured as nothing anybody had heard of).
    /// </summary>
    public static void ScanPath()
    {
        string path = Horizon.Core.Processes.ProcessEnvironment.Path();
        if (path == pathScanned || Interlocked.Exchange(ref pathStarted, 1) == 1)
            return;

        Task.Run(() =>
        {
            var found = new HashSet<string>(Names);
            string[] extensions = OperatingSystem.IsWindows()
                ? (Horizon.Core.Processes.ProcessEnvironment.Variable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
                : [];
            foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (!Directory.Exists(folder))
                        continue;
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        string name = Path.GetFileName(file);
                        if (OperatingSystem.IsWindows())
                        {
                            string extension = Path.GetExtension(name);
                            if (!Array.Exists(extensions, e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            found.Add(name);
                            found.Add(Path.GetFileNameWithoutExtension(name));
                        }
                        else
                        {
                            found.Add(name);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }

            pathCommands = found;
            pathGeneration++;
            pathScanned = path;
            Volatile.Write(ref pathStarted, 0);
        });
    }
}
