using System.Diagnostics;
using System.Text.RegularExpressions;
using Horizon.Core.Processes;

namespace Horizon.UI.Scripting;

/// <summary>Shared discovery and stdio launch rules for language servers and their version probes.</summary>
public static partial class LanguageServerProgram
{
    private static string SearchPath()
    {
        string path = ProcessEnvironment.Path();
        // npm's default global bin directory need not be in the launching application's environment.
        if (OperatingSystem.IsWindows())
            path += Path.PathSeparator + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        return path;
    }

    public static string? Locate(string command) => LocateIn(command, SearchPath());

    internal static string? LocateIn(string command, string searchPath)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        string[] endings = OperatingSystem.IsWindows() && !Path.HasExtension(command)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (string folder in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string ending in endings)
            {
                try
                {
                    string candidate = Path.Combine(folder.Trim('"'), command + ending);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    /// <summary>npm's Windows shim is a batch file; start its JavaScript entry point directly with Node.</summary>
    public static ProcessStartInfo StartInfo(string command, IReadOnlyList<string> arguments)
    {
        string path = SearchPath();
        string program = LocateIn(command, path) ?? command;
        string? script = null;
        if (OperatingSystem.IsWindows() && Path.GetExtension(program).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            && File.Exists(program) && new FileInfo(program).Length <= 65536)
        {
            Match match = NpmEntry().Match(File.ReadAllText(program));
            if (match.Success)
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(program))!;
                script = Path.GetFullPath(Path.Combine(directory, match.Groups["script"].Value));
                string localNode = Path.Combine(directory, "node.exe");
                program = File.Exists(localNode) ? localNode : LocateIn("node", path)
                    ?? throw new FileNotFoundException("Node.js is required to start this npm language server.");
                if (!File.Exists(script)) throw new FileNotFoundException("The npm language server entry point is missing.", script);
            }
        }

        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var (key, value) in ProcessEnvironment.Current()) info.Environment[key] = value;
        info.Environment["PATH"] = path;
        if (script is not null) info.ArgumentList.Add(script);
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    [GeneratedRegex("\"%(?:dp0%|~dp0)[\\\\/](?<script>node_modules[\\\\/][^\"\\r\\n]+\\.(?:[cm]?js))\"", RegexOptions.IgnoreCase)]
    private static partial Regex NpmEntry();
}
