using System.Diagnostics;
using System.Text;

using Horizon.Logging;

namespace Horizon.UI.Components;

/// <summary>What kind of thing a profile starts, a process with a terminal or something the application runs itself.</summary>
public enum TerminalProfileKind
{
    /// <summary>A command line started in a pseudo terminal, a shell.</summary>
    Shell,

    /// <summary>
    /// Something the application feeds the terminal itself, with no process behind it, the HIDL REPL in Hex. The
    /// panel asks its owner to start one (<see cref="TerminalPanel.OnStartProfile"/>) rather than starting a process.
    /// </summary>
    Embedded
}

/// <summary>
/// A shell to open a terminal with, what the new tab menu lists. A name, the command line and an icon, and the kind
/// (a process, or something embedded like the REPL). The ones a machine has are found by
/// <see cref="TerminalProfiles.Discover()"/>, an application adds its own.
/// </summary>
public sealed class TerminalProfile
{
    /// <summary>What the menu calls it, "PowerShell", "Ubuntu".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>A short name a settings file knows it by, "pwsh", "wsl:Ubuntu". The name in lower case when nobody says.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The command line to start, as a shell would read it, empty for an embedded profile.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>One of the icons every skin has (UISymbols), "terminal-powershell", "terminal-cmd", "terminal-bash", "terminal-linux".</summary>
    public string Icon { get; set; } = "terminal";

    /// <summary>Where it starts, null for the terminal's own folder.</summary>
    public string? WorkingDirectory { get; set; }

    public TerminalProfileKind Kind { get; set; } = TerminalProfileKind.Shell;

    /// <summary>Whatever the application wants to keep with it, the plugin that added it say.</summary>
    public object? Tag { get; set; }

    public TerminalProfile() { }

    public TerminalProfile(string name, string command, string icon = "terminal", string? id = null)
    {
        Name = name;
        Command = command;
        Icon = icon;
        Id = id ?? name.ToLowerInvariant().Replace(' ', '_');
    }

    public override string ToString() => Name;
}

/// <summary>
/// Finds the shells a machine has. On Windows that is PowerShell 7 (pwsh) when it is installed, Windows PowerShell,
/// cmd, Git Bash where Git is, and every WSL distribution, asked of <c>wsl -l -q</c> (which answers in UTF-16, as it
/// always has). Elsewhere it is what $SHELL says and bash, zsh and fish where they are. The looking is done through
/// functions handed in so a test can say what is on the machine without having it.
/// </summary>
public static class TerminalProfiles
{
    private static readonly Lock discoverLock = new();
    private static List<TerminalProfile>? cachedProfiles;
    private static string? cachedEnvironmentKey;
    private static bool cachedIncludesWsl;
    private static int version;

    /// <summary>Monotonic version that increments whenever asynchronously discovered profiles (such as WSL distributions) are merged.</summary>
    public static int Version => Volatile.Read(ref version);

    /// <summary>
    /// The profiles of this machine, in the order a menu lists them, the usual shell first. Cached until the relevant
    /// environment changes or <see cref="InvalidateCache"/> is called. On Windows, native shells are returned immediately
    /// without blocking on <c>wsl.exe</c>; WSL distributions are merged asynchronously and increment <see cref="Version"/>.
    /// </summary>
    public static List<TerminalProfile> Discover()
    {
        string envKey = OperatingSystem.IsWindows()
            ? Horizon.Core.Processes.ProcessEnvironment.Variable("PATH") ?? string.Empty
            : Environment.GetEnvironmentVariable("SHELL") ?? string.Empty;

        Task<string?>? task = OperatingSystem.IsWindows() ? EnsureWslPrefetch() : null;
        bool wslReady = task is { IsCompletedSuccessfully: true };
        string? wslResult = wslReady ? task!.Result : null;

        lock (discoverLock)
        {
            if (cachedProfiles is null || !string.Equals(cachedEnvironmentKey, envKey, StringComparison.Ordinal))
            {
                cachedProfiles = OperatingSystem.IsWindows()
                    ? DiscoverWindows(File.Exists, Horizon.Core.Processes.ProcessEnvironment.Variable, () => wslResult)
                    : DiscoverUnix(File.Exists, Environment.GetEnvironmentVariable);
                cachedEnvironmentKey = envKey;
                cachedIncludesWsl = !OperatingSystem.IsWindows() || wslReady;
            }
            else if (OperatingSystem.IsWindows() && !cachedIncludesWsl && wslReady)
            {
                if (AppendWslProfilesLocked(cachedProfiles, wslResult, checkWslExists: true))
                    Interlocked.Increment(ref version);
                cachedIncludesWsl = true;
            }

            var copy = new List<TerminalProfile>(cachedProfiles.Count);
            for (int i = 0; i < cachedProfiles.Count; i++)
            {
                TerminalProfile p = cachedProfiles[i];
                copy.Add(new TerminalProfile(p.Name, p.Command, p.Icon, p.Id)
                {
                    WorkingDirectory = p.WorkingDirectory,
                    Kind = p.Kind,
                    Tag = p.Tag
                });
            }
            return copy;
        }
    }

    /// <summary>Clears the cached result of <see cref="Discover()"/> so the next call re-scans the machine.</summary>
    public static void InvalidateCache()
    {
        lock (discoverLock)
        {
            cachedProfiles = null;
            cachedEnvironmentKey = null;
            cachedIncludesWsl = false;
        }
    }

    /// <summary>The Windows profiles, told what exists, what the environment says and what wsl lists.</summary>
    public static List<TerminalProfile> DiscoverWindows(Func<string, bool> exists, Func<string, string?> environment, Func<string?> wslList)
    {
        var found = new List<TerminalProfile>();
        string programFiles = environment("ProgramFiles") ?? @"C:\Program Files";
        string programFilesX86 = environment("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        string localAppData = environment("LocalAppData") ?? string.Empty;
        string system = Path.Combine(environment("SystemRoot") ?? @"C:\Windows", "System32");

        if (OnPath(exists, environment, "pwsh.exe") || exists(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe")))
            found.Add(new TerminalProfile("PowerShell", "pwsh.exe -NoLogo", "terminal-powershell", "pwsh"));

        found.Add(new TerminalProfile("Windows PowerShell", "powershell.exe -NoLogo", "terminal-powershell", "powershell"));
        found.Add(new TerminalProfile("Command Prompt", "cmd.exe", "terminal-cmd", "cmd"));

        // Git Bash, wherever Git was installed, the bin one (the usr one is the plumbing)
        foreach (string root in new[] { programFiles, programFilesX86, Path.Combine(localAppData, "Programs") })
        {
            string bash = Path.Combine(root, "Git", "bin", "bash.exe");
            if (root.Length > 0 && exists(bash))
            {
                found.Add(new TerminalProfile("Git Bash", $"\"{bash}\" --login -i", "terminal-bash", "git_bash"));
                break;
            }
        }

        if (exists(Path.Combine(system, "wsl.exe")))
        {
            foreach (string distribution in ParseWslList(wslList()))
                found.Add(new TerminalProfile(distribution, $"wsl.exe -d {distribution}", "terminal-linux", $"wsl:{distribution}"));
        }

        return found;
    }

    /// <summary>The profiles of a Unix box, $SHELL first and then the usual suspects that are there.</summary>
    public static List<TerminalProfile> DiscoverUnix(Func<string, bool> exists, Func<string, string?> environment)
    {
        var found = new List<TerminalProfile>();
        string? own = environment("SHELL");
        if (own is { Length: > 0 })
            found.Add(new TerminalProfile(Path.GetFileName(own), own, "terminal-bash", Path.GetFileName(own)));

        foreach (var (name, icon) in new[] { ("bash", "terminal-bash"), ("zsh", "terminal"), ("fish", "terminal") })
        {
            foreach (string folder in new[] { "/bin", "/usr/bin", "/usr/local/bin", "/opt/homebrew/bin" })
            {
                string path = folder + "/" + name;
                if (exists(path) && !found.Exists(profile => profile.Command == path || profile.Id == name))
                {
                    found.Add(new TerminalProfile(name, path, icon, name));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The distributions out of what <c>wsl -l -q</c> printed. It writes UTF-16 with a byte order mark when its
    /// output is a pipe, which read as UTF-8 is every other byte a nul, so a text with nuls in it is read again
    /// as UTF-16. One name a line, blank lines and the docker ones left out. So is anything that isn't letters, digits,
    /// dots, dashes and underscores, which is all wsl lets a distribution be called. The rest is wsl explaining that there
    /// aren't any ("has no installed distributions", "https://aka.ms/wslstore"), and a plugin naming commands after it fails.
    /// </summary>
    public static List<string> ParseWslList(string? output)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(output))
            return names;

        if (output.Contains('\0'))
        {
            byte[] raw = Encoding.Latin1.GetBytes(output);
            output = Encoding.Unicode.GetString(raw);
        }

        foreach (string line in output.Split('\n'))
        {
            string name = line.Trim('\r', ' ', '\t', '\uFEFF', '\0');
            if (name.Length == 0 || !name.All(IsDistributionChar) || name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                continue;

            names.Add(name);
        }

        return names;
    }

    private static bool IsDistributionChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';

    private static bool OnPath(Func<string, bool> exists, Func<string, string?> environment, string file)
    {
        foreach (string folder in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = folder.Trim().Trim('"');
            if (trimmed.Length > 0 && exists(Path.Combine(trimmed, file)))
                return true;
        }

        return false;
    }

    // How long wsl gets to list its distributions. It answers in a few hundred milliseconds when it's well, and on a
    // machine where it isn't (the service waking up cold, a half installed one, the stub asking for a key) it can sit
    // there for minutes, on the main thread, before the editor or the terminal has drawn a thing
    private const int WSL_TIMEOUT_MS = 2000;

    // Asked once a run on the pool, the distributions don't change while the program is up, and Dawn and Dusk both discover
    private static Task<string?>? wslTask;

    /// <summary>
    /// Starts asking WSL for its distributions on the pool, for a program to call as it starts so the answer is there by
    /// the time its terminal is made. <see cref="Discover"/> also starts this lazily without blocking; when the task
    /// finishes, WSL profiles are merged and <see cref="Version"/> increments.
    /// </summary>
    public static void Prefetch()
    {
        if (OperatingSystem.IsWindows())
            EnsureWslPrefetch();
    }

    private static Task<string?> EnsureWslPrefetch()
    {
        Task<string?>? existing = Volatile.Read(ref wslTask);
        if (existing is not null)
            return existing;

        lock (discoverLock)
        {
            if (wslTask is not null)
                return wslTask;

            wslTask = Task.Run(AskWslAndMerge);
            return wslTask;
        }
    }

    private static string? AskWslAndMerge()
    {
        string? output = AskWsl();
        MergeWslOutput(output, checkWslExists: true);
        return output;
    }

    private static void MergeWslOutput(string? output, bool checkWslExists)
    {
        bool changed = false;
        lock (discoverLock)
        {
            if (cachedProfiles is not null && !cachedIncludesWsl)
            {
                changed = AppendWslProfilesLocked(cachedProfiles, output, checkWslExists);
                cachedIncludesWsl = true;
            }
        }

        if (changed)
        {
            Interlocked.Increment(ref version);
            Horizon.Engine.GameEngine.Instance?.WindowManager?.Wake();
        }
    }

    private static bool AppendWslProfilesLocked(List<TerminalProfile> target, string? output, bool checkWslExists)
    {
        if (checkWslExists)
        {
            string system = Path.Combine(Horizon.Core.Processes.ProcessEnvironment.Variable("SystemRoot") ?? @"C:\Windows", "System32");
            if (!File.Exists(Path.Combine(system, "wsl.exe")))
                return false;
        }

        List<string> distributions = ParseWslList(output);
        bool added = false;
        for (int i = 0; i < distributions.Count; i++)
        {
            string distribution = distributions[i];
            string id = $"wsl:{distribution}";
            bool exists = false;
            for (int j = 0; j < target.Count; j++)
            {
                if (string.Equals(target[j].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                target.Add(new TerminalProfile(distribution, $"wsl.exe -d {distribution}", "terminal-linux", id));
                added = true;
            }
        }

        return added;
    }

    internal static Task<string?>? SwapWslTaskForTests(Task<string?>? replacement)
    {
        lock (discoverLock)
        {
            Task<string?>? previous = wslTask;
            wslTask = replacement;
            cachedProfiles = null;
            cachedEnvironmentKey = null;
            cachedIncludesWsl = false;
            return previous;
        }
    }

    internal static void MergeWslForTests(string? output) => MergeWslOutput(output, checkWslExists: false);

    /// <summary>Runs wsl -l -q and gives back what it wrote, as the bytes it wrote read one to a char, null when it couldn't be run, failed or took too long.</summary>
    private static string? AskWsl()
    {
        try
        {
            using var wsl = Process.Start(new ProcessStartInfo("wsl.exe", "-l -q")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Latin1
            });
            if (wsl is null)
                return null;

            // Nothing to read from, so a wsl that wants a key pressed gets an end instead, and both pipes are emptied on
            // the side so a full one can't stall it. The wait is the whole of it, not what comes after the reading
            wsl.StandardInput.Close();
            Task<string> output = wsl.StandardOutput.ReadToEndAsync();
            _ = wsl.StandardError.ReadToEndAsync();

            if (!wsl.WaitForExit(WSL_TIMEOUT_MS) || !output.Wait(WSL_TIMEOUT_MS))
            {
                Log.Warning($"[Terminal] wsl didn't list its distributions within {WSL_TIMEOUT_MS} ms, the terminal goes on without them.");
                try { wsl.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            // Without a distribution, or with WSL switched off, it fails and writes why down the same pipe, which isn't a list
            return wsl.ExitCode == 0 ? output.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
