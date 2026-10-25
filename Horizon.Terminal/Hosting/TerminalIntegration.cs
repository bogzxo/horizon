namespace Horizon.UI.Components;

/// <summary>
/// Shell integration, the marks (OSC 133) and the folder (OSC 7) a shell says when it is given a script for it as it
/// starts, never written into anybody's profile. PowerShell gets Assets/terminal/shell/dusk.ps1 dot sourced after its
/// profile, bash (Git Bash, a WSL distribution, bash elsewhere) gets dusk.bash as its init file, which reads the login
/// files first. zsh (a Mac's shell) is started with ZDOTDIR at shell/zsh, whose files read the user's own and then
/// dusk.zsh, and fish runs dusk.fish after its config with --init-command. cmd says its prompt's marks and its folder through the PROMPT variable it is started with (its $e is an
/// escape), it can't say where a command's output starts or how it ended, so those are worked out round it.
/// </summary>
public static class TerminalIntegration
{
    public static string Scripts => Path.Combine(AppContext.BaseDirectory, TerminalHostOptions.ENGINE_ASSETS, "shell");

    /// <summary>Which kind of shell a profile is, for its script, "powershell", "bash" or "" for one that has none.</summary>
    public static string KindOf(TerminalProfile profile)
    {
        if (profile.Kind != TerminalProfileKind.Shell)
            return string.Empty;

        string command = profile.Command.ToLowerInvariant();
        if (profile.Id is "pwsh" or "powershell" || command.Contains("pwsh") || command.Contains("powershell"))
            return "powershell";
        if (profile.Id.StartsWith("wsl:", StringComparison.Ordinal) || profile.Id == "git_bash" || command.EndsWith("bash") || command.Contains("bash.exe") || command.EndsWith("/bash"))
            return "bash";
        if (profile.Id == "cmd" || command is "cmd" or "cmd.exe" || command.EndsWith("\\cmd.exe", StringComparison.Ordinal))
            return "cmd";
        if (ProgramOf(command) is "zsh")
            return "zsh";
        if (ProgramOf(command) is "fish")
            return "fish";
        return string.Empty;
    }

    /// <summary>cmd's prompt with the marks round it and the folder said, whatever PROMPT the machine had.</summary>
    public static string CmdPrompt(string? prompt) => $"$e]133;A$e\\$e]9;9;$P$e\\{(string.IsNullOrEmpty(prompt) ? "$P$G" : prompt)}$e]133;B$e\\";

    /// <summary>
    /// What a profile's process is started with on top of the machine's environment. The file the shell writes what
    /// commands it has into (DUSK_COMMANDS_FILE, see <see cref="TerminalCommandLookup"/>), passed through to WSL, and cmd's
    /// prompt with its marks and the folder of the scripts on its PATH, for dawn.cmd (cmd has no functions to give it the
    /// dawn command the other shells get from their scripts).
    /// </summary>
    public static Dictionary<string, string> Environment(TerminalProfile profile, string? commandsFile, bool integration)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!integration)
            return environment;

        string kind = KindOf(profile);
        if (kind == "cmd")
        {
            environment["PROMPT"] = CmdPrompt(System.Environment.GetEnvironmentVariable("PROMPT"));
            if (Directory.Exists(Scripts))
                environment["PATH"] = Scripts + Path.PathSeparator + Horizon.Core.Processes.ProcessEnvironment.Path();
        }

        // zsh reads its dotfiles from ZDOTDIR, which is the integration's, and the user's own one is passed along for it to read
        if (kind == "zsh" && Directory.Exists(Path.Combine(Scripts, "zsh")))
        {
            environment["ZDOTDIR"] = Path.Combine(Scripts, "zsh");
            environment["DUSK_USER_ZDOTDIR"] = System.Environment.GetEnvironmentVariable("ZDOTDIR") ?? string.Empty;
        }

        // Which side the terminal is on, for the dawn command of the Unix shells' scripts: a Windows terminal wants a
        // Windows path (a WSL distribution's shell, Git Bash's), one on Linux or a Mac the path as it is, wslpath or not
        var toWsl = new List<string>();
        if (kind is "bash" or "zsh" or "fish")
        {
            environment["DUSK_HOST"] = OperatingSystem.IsWindows() ? "windows" : "unix";
            toWsl.Add("DUSK_HOST");
        }

        if (commandsFile is not null && kind is "powershell" or "bash" or "zsh" or "fish")
        {
            environment["DUSK_COMMANDS_FILE"] = commandsFile.Replace('\\', '/');
            toWsl.Add("DUSK_COMMANDS_FILE/p");
        }

        if (profile.Id.StartsWith("wsl:", StringComparison.Ordinal) && toWsl.Count > 0)
        {
            string passed = System.Environment.GetEnvironmentVariable("WSLENV") ?? string.Empty;
            environment["WSLENV"] = (passed.Length > 0 ? passed + ":" : string.Empty) + string.Join(':', toWsl);
        }

        return environment;
    }

    /// <summary>The file a profile's shell writes its commands into, one a profile in the user's folder.</summary>
    public static string CommandsFile(TerminalProfile profile)
    {
        var name = new System.Text.StringBuilder("commands-");
        foreach (char c in profile.Id)
            name.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return Path.Combine(TerminalFiles.UserFolder, "cache", name.Append(".txt").ToString());
    }

    /// <summary>The command line a profile is started with to say its marks, its own when its shell has no script.</summary>
    public static string Command(TerminalProfile profile, string? scripts = null)
    {
        scripts ??= Scripts;
        string command = profile.Command;
        switch (KindOf(profile))
        {
            case "powershell":
            {
                string script = Path.Combine(scripts, "dusk.ps1");
                if (!File.Exists(script))
                    return command;

                // a stock Windows install is Restricted and won't dot source the script, Bypass is for this process only
                // and writes no policy anywhere. One the profile already picks is left alone
                string policy = HasExecutionPolicy(command) ? string.Empty : " -ExecutionPolicy Bypass";
                return $"{command}{policy} -NoExit -Command \". '{script.Replace("'", "''")}'\"";
            }

            case "bash":
            {
                string script = Path.Combine(scripts, "dusk.bash");
                if (!File.Exists(script))
                    return command;

                if (profile.Id.StartsWith("wsl:", StringComparison.Ordinal))
                    return $"{command} -- bash --init-file '{WslPath(script)}' -i";

                // Git Bash's own login is read by the script, a second --login would skip it
                string shell = command.Replace(" --login", string.Empty, StringComparison.Ordinal).Replace(" -i", string.Empty, StringComparison.Ordinal);
                return $"{shell} --init-file '{(OperatingSystem.IsWindows() ? MsysPath(script) : script)}' -i";
            }

            case "fish":
            {
                string script = Path.Combine(scripts, "dusk.fish");
                return File.Exists(script) ? $"{command} --init-command \"source '{script.Replace("'", "\\'")}'\"" : command;
            }

            default:
                return command;
        }
    }

    // Whether a PowerShell command line gives its own policy, -ExecutionPolicy or how PowerShell lets it be cut short (-ep, -ex, -exec)
    private static bool HasExecutionPolicy(string command) =>
        System.Text.RegularExpressions.Regex.IsMatch(command, @"(^|\s)[-/](ep|ex[a-z]*)(\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // The program a command line starts, its file's name without a folder or .exe, lower case
    private static string ProgramOf(string command)
    {
        string first = command.TrimStart().Split(' ', 2)[0].Trim('"');
        return Path.GetFileNameWithoutExtension(first.Replace('\\', '/').Split('/')[^1]);
    }

    /// <summary>A Windows path as Git Bash writes it, C:\Users to /c/Users.</summary>
    public static string MsysPath(string path) =>
        path.Length >= 2 && path[1] == ':' ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/') : path.Replace('\\', '/');

    /// <summary>A Windows path as WSL sees it, C:\Users to /mnt/c/Users.</summary>
    public static string WslPath(string path) => "/mnt" + MsysPath(path);
}
