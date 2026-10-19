using System.Diagnostics;
using System.Text;

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
    /// <summary>The profiles of this machine, in the order a menu lists them, the usual shell first.</summary>
    public static List<TerminalProfile> Discover() =>
        OperatingSystem.IsWindows()
            ? DiscoverWindows(File.Exists, Horizon.Core.Processes.ProcessEnvironment.Variable, AskWsl)
            : DiscoverUnix(File.Exists, Environment.GetEnvironmentVariable);

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
    /// as UTF-16. One name a line, blank lines and the docker ones left out.
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
            if (name.Length == 0 || name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                continue;

            names.Add(name);
        }

        return names;
    }

    private static bool OnPath(Func<string, bool> exists, Func<string, string?> environment, string file)
    {
        foreach (string folder in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (exists(Path.Combine(folder.Trim('"'), file)))
                return true;
        }

        return false;
    }

    /// <summary>Runs wsl -l -q and gives back what it wrote, as the bytes it wrote read one to a char, null when it couldn't be run.</summary>
    private static string? AskWsl()
    {
        try
        {
            using var wsl = Process.Start(new ProcessStartInfo("wsl.exe", "-l -q")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Latin1
            });
            if (wsl is null)
                return null;

            string output = wsl.StandardOutput.ReadToEnd();
            if (!wsl.WaitForExit(3000))
                wsl.Kill();

            return output;
        }
        catch
        {
            return null;
        }
    }
}
