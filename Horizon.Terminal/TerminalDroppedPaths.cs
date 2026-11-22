using System.Text;

namespace Horizon.UI.Components;

/// <summary>
/// Files dropped on a terminal, as the shell in it reads them: each path quoted the way that shell quotes (only when it
/// has to be), a space between them and one after, the way Windows Terminal pastes a drop. A WSL distribution is handed
/// its own /mnt path for a Windows one.
/// </summary>
public static class TerminalDroppedPaths
{
    /// <summary>How a shell quotes, worked out from the command its profile starts.</summary>
    public enum Quoting
    {
        /// <summary>sh, bash, zsh, fish: single quotes, a quote in one closed and reopened round an escaped one.</summary>
        Posix,

        /// <summary>WSL from Windows: a POSIX shell given a /mnt path for a drive's.</summary>
        Wsl,

        /// <summary>PowerShell: single quotes, a quote in one doubled.</summary>
        PowerShell,

        /// <summary>cmd: double quotes, which can't hold one, a quote in a path can't be on Windows anyway.</summary>
        Cmd
    }

    /// <summary>The quoting of a profile's shell, the system's usual one when there's no profile.</summary>
    public static Quoting QuotingOf(TerminalProfile? profile)
    {
        string command = profile?.Command.ToLowerInvariant() ?? string.Empty;
        string id = profile?.Id.ToLowerInvariant() ?? string.Empty;
        if (id.StartsWith("wsl", StringComparison.Ordinal) || command.StartsWith("wsl", StringComparison.Ordinal) || command.Contains("\\wsl.exe", StringComparison.Ordinal))
            return Quoting.Wsl;
        if (command.Contains("pwsh", StringComparison.Ordinal) || command.Contains("powershell", StringComparison.Ordinal))
            return Quoting.PowerShell;
        if (command.Contains("cmd.exe", StringComparison.Ordinal) || command == "cmd" || id == "cmd")
            return Quoting.Cmd;
        if (command.Length == 0 && OperatingSystem.IsWindows())
            return Quoting.PowerShell;
        return Quoting.Posix;
    }

    /// <summary>What dropping the paths types into the shell, each quoted, a space after every one.</summary>
    public static string Typed(IEnumerable<string> paths, TerminalProfile? profile)
    {
        Quoting quoting = QuotingOf(profile);
        var typed = new StringBuilder();
        foreach (string path in paths)
            if (!string.IsNullOrEmpty(path))
                typed.Append(Quoted(path, quoting)).Append(' ');
        return typed.ToString();
    }

    /// <summary>A path as the shell reads it, quoted only when there's something in it the shell would take apart.</summary>
    public static string Quoted(string path, Quoting quoting)
    {
        if (quoting == Quoting.Wsl)
            path = InWsl(path);

        if (!NeedsQuotes(path, quoting))
            return path;

        return quoting switch
        {
            Quoting.Cmd => "\"" + path + "\"",
            Quoting.PowerShell => "'" + path.Replace("'", "''") + "'",
            _ => "'" + path.Replace("'", "'\\''") + "'"
        };
    }

    /// <summary>A Windows path as WSL sees it, C:\Users\me to /mnt/c/Users/me. Anything else as it is.</summary>
    public static string InWsl(string path)
    {
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
        return path;
    }

    private static bool NeedsQuotes(string path, Quoting quoting)
    {
        if (path.Length == 0)
            return true;

        // What each shell takes apart outside quotes. cmd's set is the one Windows Terminal quotes for
        string special = quoting switch
        {
            Quoting.Cmd => " &()[]{}^=;!'+,`~",
            Quoting.PowerShell => " &()[]{}@$;'`\",#|<>",
            _ => " &()[]{}$;'`\"\\|<>*?!#~"
        };

        foreach (char c in path)
            if (char.IsWhiteSpace(c) || special.Contains(c))
                return true;
        return false;
    }
}
