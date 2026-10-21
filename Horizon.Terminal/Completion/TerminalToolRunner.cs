using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Horizon.UI.Components;

/// <summary>
/// Runs a command-line tool for what it says about itself (its help, git's branches) and hands back what it printed.
/// Never on the thread that asked, never with anything to read from, never for longer than a couple of seconds, never
/// with a pager or colours, and only ever with words that look like a subcommand or a flag. On Windows a tool that is a
/// .cmd or a .bat (npm, yarn, pnpm) is found on the PATH and run through cmd, which is the only thing that can.
/// </summary>
public static partial class TerminalToolRunner
{
    private const int TIMEOUT_MS = 2500;

    // What it prints past this is cut off, a help that long is a manual and the completion has what it needs
    private const int MOST_OUTPUT = 256 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9][\w:./=,%()-]*$")]
    private static partial Regex SafeArgument();

    /// <summary>Whether an argument is one it's willing to pass on, a word or a flag and nothing a shell would read anything into.</summary>
    public static bool IsSafe(string argument) => SafeArgument().IsMatch(argument) || (argument.StartsWith('-') && SafeArgument().IsMatch(argument.TrimStart('-')));

    /// <summary>What a tool printed, its output and its errors together (plenty of tools write their help to the errors), null when it couldn't be run.</summary>
    public static async Task<string?> RunAsync(string tool, IReadOnlyList<string> arguments, string? folder)
    {
        if (arguments.Any(argument => !IsSafe(argument)) || !IsSafe(tool))
            return null;

        if (Find(tool) is not { } program)
            return null;

        var start = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = folder is not null && Directory.Exists(folder) ? folder : Environment.CurrentDirectory
        };

        string extension = Path.GetExtension(program).ToLowerInvariant();
        if (OperatingSystem.IsWindows() && extension is ".cmd" or ".bat")
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(program);
        }
        else
        {
            start.FileName = program;
        }

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        // Plain text and nothing waiting on a person, git's pager and its help opening a browser included
        start.Environment["PAGER"] = "cat";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;

            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();

            using var cancel = new CancellationTokenSource(TIMEOUT_MS);
            try
            {
                await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
                await Task.WhenAll(output, errors).WaitAsync(cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string said = output.Result.Length > 0 ? output.Result + "\n" + errors.Result : errors.Result;
            return said.Length > MOST_OUTPUT ? said[..MOST_OUTPUT] : said;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Where tools were found, by name, so the PATH is walked once a tool
    private static readonly Dictionary<string, string?> found = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly Lock foundLock = new();

    /// <summary>Where a tool is on the PATH, with the extension Windows runs it by, null when it isn't there.</summary>
    public static string? Find(string tool)
    {
        lock (foundLock)
        {
            if (found.TryGetValue(tool, out string? known))
                return known;
        }

        string? path = Search(tool);
        lock (foundLock)
            found[tool] = path;
        return path;
    }

    private static string? Search(string tool)
    {
        string[] extensions = OperatingSystem.IsWindows()
            ? [string.Empty, .. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)]
            : [string.Empty];

        foreach (string folder in (Horizon.Core.Processes.ProcessEnvironment.Variable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                // On Windows a name with no extension isn't something it runs, npm next to npm.cmd is the shell script for bash
                if (OperatingSystem.IsWindows() && extension.Length == 0 && Path.GetExtension(tool).Length == 0)
                    continue;

                try
                {
                    string candidate = Path.Combine(folder.Trim('"'), tool + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception)
                {
                    // a folder on the PATH that isn't one
                }
            }
        }

        return null;
    }
}
