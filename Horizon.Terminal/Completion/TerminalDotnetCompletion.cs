using System.Collections.Concurrent;
using System.Diagnostics;

namespace Horizon.UI.Components;

/// <summary>Uses the selected SDK's completion protocol, including its NuGet and project-aware providers.</summary>
public sealed class TerminalDotnetCompletion
{
    public static TerminalDotnetCompletion Shared { get; } = new();
    private sealed record Answer(string[] Values, long At);
    private readonly ConcurrentDictionary<string, Answer> answers = new();
    private readonly ConcurrentDictionary<string, byte> pending = new();
    private readonly ConcurrentDictionary<string, string> latest = new();
    private readonly SemaphoreSlim slots = new(2);
    private readonly Func<string, string?, Task<string?>> run;
    private int version;

    public TerminalDotnetCompletion(Func<string, string?, Task<string?>>? run = null) => this.run = run ?? Query;
    public int Version => Volatile.Read(ref version);

    public List<TerminalLineCompletion> Complete(IReadOnlyList<string> words, string word, string? folder)
    {
        // The command is data to dotnet complete, never a shell command to execute.
        string command = "dotnet " + string.Join(' ', words.Skip(1).Select(Quote));
        if (words.Count > 1) command += " ";
        command += word;
        string where = folder ?? Environment.CurrentDirectory;
        string key = where + "\n" + command;
        latest[where] = key;
        answers.TryGetValue(key, out var answer);
        if ((answer is null || Environment.TickCount64 - answer.At > 10000) && pending.TryAdd(key, 0))
            _ = Learn(key, command, where);
        return (answer?.Values ?? []).Where(value => value.StartsWith(word, StringComparison.OrdinalIgnoreCase) && !value.Equals(word, StringComparison.OrdinalIgnoreCase))
            .Select(value => new TerminalLineCompletion(value, false, false, ".NET SDK")).ToList();
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) || value.IndexOfAny(['"', '|', '&', ';', '(', ')']) >= 0
        ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private async Task Learn(string key, string command, string folder)
    {
        try
        {
            await Task.Delay(150).ConfigureAwait(false);
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                if (latest.GetValueOrDefault(folder) != key) return;
                string? output = await run(command, folder).ConfigureAwait(false);
                string[] values = (output ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(value => !value.Any(char.IsControl)).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
                if (answers.Count >= 128) answers.Clear();
                answers[key] = new(values, Environment.TickCount64);
                Interlocked.Increment(ref version);
            }
            finally { slots.Release(); }
        }
        catch (Exception) { answers[key] = new([], Environment.TickCount64); Interlocked.Increment(ref version); }
        finally { pending.TryRemove(key, out _); }
    }

    public static async Task<string?> Query(string command, string? folder)
    {
        if (command.Length > 8192 || command.IndexOfAny(['\r', '\n', '\0']) >= 0 || TerminalToolRunner.Find("dotnet") is not { } executable) return null;
        var info = new ProcessStartInfo(executable) { WorkingDirectory = folder ?? Environment.CurrentDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "complete", "--position", command.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), command }) info.ArgumentList.Add(argument);
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                return null;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            string? result = process.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
            return await ProjectFrameworks(executable, command, folder).ConfigureAwait(false) ?? result;
        }
        catch (Exception) { return null; }
    }
    private static async Task<string?> ProjectFrameworks(string executable, string command, string? folder)
    {
        var words = TerminalShellCompletion.CommandWords(command, command.Length);
        if (words.Count < 4 || words[1] is not ("build" or "test" or "run" or "publish" or "pack")) return null;
        int option = words.FindLastIndex(word => word is "--framework" or "-f");
        if (option < 0 || option < words.Count - 2) return null;
        string? configuration = null, runtime = null;
        for (int i = 2; i < option; i++)
        {
            if (words[i] is "--configuration" or "-c" && i + 1 < option) configuration = words[++i];
            else if (words[i].StartsWith("--configuration=", StringComparison.Ordinal)) configuration = words[i][16..];
            else if (words[i] is "--runtime" or "-r" && i + 1 < option) runtime = words[++i];
            else if (words[i].StartsWith("--runtime=", StringComparison.Ordinal)) runtime = words[i][10..];
        }
        string? project = null;
        for (int i = 2; i < option; i++)
        {
            if (words[i] == "--project" && i + 1 < option) project = words[++i];
            else if (words[i].StartsWith("--project=", StringComparison.Ordinal)) project = words[i][10..];
        }
        project ??= words.Skip(2).Take(option - 2).FirstOrDefault(word => Path.GetExtension(word).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj");
        string where = folder ?? Environment.CurrentDirectory;
        if (project is not null) project = Path.GetFullPath(project, where);
        string? projectFolder = project is not null && Directory.Exists(project) ? project : project is null && (configuration is not null || runtime is not null) ? where : null;
        if (projectFolder is not null)
        {
            var projects = Directory.EnumerateFiles(projectFolder)
                .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj").Take(2).ToArray();
            if (projects.Length != 1) return null;
            project = projects[0];
        }
        if (project is null) return null;
        if (!File.Exists(project)) return null;
        var info = new ProcessStartInfo(executable) { WorkingDirectory = folder ?? Environment.CurrentDirectory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "msbuild", project, "-nologo", "-getProperty:TargetFramework,TargetFrameworks" }) info.ArgumentList.Add(argument);
        if (configuration is not null) info.ArgumentList.Add("-property:Configuration=" + configuration.Replace("%", "%25").Replace(";", "%3B").Replace(",", "%2C"));
        if (runtime is not null) info.ArgumentList.Add("-property:RuntimeIdentifier=" + runtime.Replace("%", "%25").Replace(";", "%3B").Replace(",", "%2C"));
        using var process = Process.Start(info);
        if (process is null) return null;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return null;
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0) return null;
        using var result = System.Text.Json.JsonDocument.Parse(await stdout.ConfigureAwait(false));
        var properties = result.RootElement.GetProperty("Properties");
        return string.Join('\n', new[] { properties.GetProperty("TargetFramework").GetString(), properties.GetProperty("TargetFrameworks").GetString() }
            .SelectMany(value => (value ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

}
