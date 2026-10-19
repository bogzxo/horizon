namespace Horizon.UI.Components;

/// <summary>
/// Shell integration, the marks (OSC 133) and the folder (OSC 7) a shell says when it is given a script for it as it
/// starts, never written into anybody's profile. PowerShell gets Assets/terminal/shell/dusk.ps1 dot sourced after its
/// profile, bash (Git Bash, a WSL distribution, bash elsewhere) gets dusk.bash as its init file, which reads the login
/// files first. cmd says its prompt's marks and its folder through the PROMPT variable it is started with (its $e is an
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

        if (commandsFile is not null && kind is "powershell" or "bash")
        {
            environment["DUSK_COMMANDS_FILE"] = commandsFile.Replace('\\', '/');
            if (profile.Id.StartsWith("wsl:", StringComparison.Ordinal))
            {
                string passed = System.Environment.GetEnvironmentVariable("WSLENV") ?? string.Empty;
                environment["WSLENV"] = (passed.Length > 0 ? passed + ":" : string.Empty) + "DUSK_COMMANDS_FILE/p";
            }
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
                return File.Exists(script) ? $"{command} -NoExit -Command \". '{script.Replace("'", "''")}'\"" : command;
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

            default:
                return command;
        }
    }

    /// <summary>A Windows path as Git Bash writes it, C:\Users to /c/Users.</summary>
    public static string MsysPath(string path) =>
        path.Length >= 2 && path[1] == ':' ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/') : path.Replace('\\', '/');

    /// <summary>A Windows path as WSL sees it, C:\Users to /mnt/c/Users.</summary>
    public static string WslPath(string path) => "/mnt" + MsysPath(path);
}
