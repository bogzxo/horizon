using System.Collections;

namespace Horizon.Core.Processes;

/// <summary>
/// The environment a program started now should get. Windows hands a process its environment once, as it starts, so a
/// terminal that has been open since before somebody added a folder to their PATH (in the system's settings, an
/// installer) went on giving every new shell the PATH it was started with, and the program wasn't found. Windows Terminal
/// reads the machine's and the user's variables afresh for every shell it starts, and so does this. What this process
/// has is the start (what it was launched with, the variables only a session has), the machine's variables and then the
/// user's are laid over it as the registry has them right now, and the PATH is the machine's, then the user's, then any
/// folder this process had that neither of those has (Hex started from a developer prompt keeps that prompt's tools).
/// Elsewhere it is this process's environment as it is.
/// </summary>
public static class ProcessEnvironment
{
    private const string PATH = "Path";

    /// <summary>Every variable a program started now should have, by name, the case of a name not mattering.</summary>
    public static Dictionary<string, string> Current()
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            all[(string)entry.Key] = (string?)entry.Value ?? string.Empty;

        if (!OperatingSystem.IsWindows())
            return all;

        string own = all.GetValueOrDefault(PATH, string.Empty);
        foreach (EnvironmentVariableTarget target in (ReadOnlySpan<EnvironmentVariableTarget>)[EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User])
        {
            foreach (DictionaryEntry entry in Saved(target))
            {
                if (!string.Equals((string)entry.Key, PATH, StringComparison.OrdinalIgnoreCase))
                    all[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
            }
        }

        all[KeyOf(all, PATH)] = PathFrom(own);
        return all;
    }

    /// <summary>The PATH a program started now should have, see the class.</summary>
    public static string Path()
    {
        string own = Environment.GetEnvironmentVariable(PATH) ?? string.Empty;
        return OperatingSystem.IsWindows() ? PathFrom(own) : own;
    }

    /// <summary>A variable a program started now should have, null for none, for whoever looks one up by name.</summary>
    public static string? Variable(string name)
    {
        if (!OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable(name);
        if (string.Equals(name, PATH, StringComparison.OrdinalIgnoreCase))
            return Path();

        return Read(name, EnvironmentVariableTarget.User) ?? Read(name, EnvironmentVariableTarget.Machine) ?? Environment.GetEnvironmentVariable(name);
    }

    /// <summary>
    /// A command line with its program found on a PATH, the program written out whole when it was only named ("pwsh
    /// -NoLogo" to the pwsh.exe of the PATH). Windows looks a program up on the PATH of whoever starts it, and that is
    /// as stale as the rest of this process's environment, a shell installed after Dusk started couldn't be started at
    /// all. A program with a folder in it, or one not found, is left as it is.
    /// </summary>
    /// <param name="path">The PATH to look on, see <see cref="Path"/>.</param>
    public static string Resolve(string commandLine, string path)
    {
        string line = commandLine.TrimStart();
        if (line.Length == 0)
            return commandLine;

        // The program is the first word, or what's between the quotes it starts with
        int end;
        string program;
        if (line[0] == '"')
        {
            end = line.IndexOf('"', 1);
            if (end < 0)
                return commandLine;
            program = line[1..end];
            end++;
        }
        else
        {
            end = line.IndexOf(' ');
            if (end < 0)
                end = line.Length;
            program = line[..end];
        }

        if (program.Contains('\\') || program.Contains('/') || program.Contains(':'))
            return commandLine;

        // As Windows does it, the name as it is when it has an extension, with .exe on the end when it hasn't
        string file = System.IO.Path.HasExtension(program) ? program : program + ".exe";
        foreach (string folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = System.IO.Path.Combine(folder.Trim('"'), file);
                if (File.Exists(candidate))
                    return "\"" + candidate + "\"" + line[end..];
            }
            catch (ArgumentException)
            {
            }
        }

        return commandLine;
    }

    /// <summary>Helper method to put the machine's PATH, the user's and what else this process's had together, each folder once.</summary>
    internal static string PathFrom(string own, string? machine = null, string? user = null)
    {
        machine ??= Read(PATH, EnvironmentVariableTarget.Machine) ?? string.Empty;
        user ??= Read(PATH, EnvironmentVariableTarget.User) ?? string.Empty;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        foreach (string list in (ReadOnlySpan<string>)[machine, user, own])
        {
            foreach (string folder in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(folder.TrimEnd('\\', '/')))
                    folders.Add(folder);
            }
        }

        return string.Join(';', folders);
    }

    // The name a variable already goes by, PATH and Path being the one variable on Windows
    private static string KeyOf(Dictionary<string, string> all, string name)
    {
        foreach (string key in all.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return name;
    }

    private static IDictionary Saved(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariables(target);
        }
        catch (Exception)
        {
            return new Hashtable();
        }
    }

    private static string? Read(string name, EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name, target);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
