using System.Collections.Concurrent;

namespace Horizon.UI.Components;

/// <summary>
/// What git's own words can be in a repository, its branches and tags, its remotes and the files with changes, which
/// no help says. Asked of git in the folder the terminal is in, kept a few seconds so typing a word doesn't run git on
/// every letter, and like the help, nothing for now and <see cref="Version"/> going up when it's in.
/// </summary>
public sealed class TerminalGitValues
{
    public static TerminalGitValues Shared { get; } = new();

    /// <summary>What a repository has, as of when git was last asked.</summary>
    public sealed record Snapshot(IReadOnlyList<string> Refs, IReadOnlyList<string> Remotes, IReadOnlyList<string> Changed, long At);

    private const long FRESH_MS = 4000;

    private readonly ConcurrentDictionary<string, Snapshot> known = new(Horizon.Core.Platform.PlatformPaths.Comparer);
    private readonly ConcurrentDictionary<string, bool> asking = new(Horizon.Core.Platform.PlatformPaths.Comparer);
    private int version;

    public int Version => Volatile.Read(ref version);

    /// <summary>What the repository a folder is in has, null while git is being asked the first time.</summary>
    public Snapshot? Of(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
            return null;

        known.TryGetValue(folder, out Snapshot? snapshot);
        bool stale = snapshot is null || Environment.TickCount64 - snapshot.At > FRESH_MS;
        if (stale && asking.TryAdd(folder, true))
            _ = Ask(folder);
        return snapshot;
    }

    private async Task Ask(string folder)
    {
        try
        {
            Task<string?> refs = TerminalToolRunner.RunAsync("git", ["for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes", "refs/tags"], folder);
            Task<string?> remotes = TerminalToolRunner.RunAsync("git", ["remote"], folder);
            Task<string?> status = TerminalToolRunner.RunAsync("git", ["status", "--porcelain"], folder);
            await Task.WhenAll(refs, remotes, status).ConfigureAwait(false);

            known[folder] = new Snapshot(Lines(refs.Result).Where(name => !name.EndsWith("/HEAD", StringComparison.Ordinal) && !name.Contains(' ')).ToArray(),
                Lines(remotes.Result).ToArray(), ChangedFiles(status.Result), Environment.TickCount64);
            Interlocked.Increment(ref version);
        }
        catch (Exception)
        {
            // not a repository, or no git, it has nothing then
        }
        finally
        {
            asking.TryRemove(folder, out _);
        }
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("fatal", StringComparison.Ordinal) && !line.StartsWith("warning", StringComparison.Ordinal));

    /// <summary>The files git status says have changes, "R  old -> new" as the new one, quotes taken off.</summary>
    public static string[] ChangedFiles(string? status)
    {
        var files = new List<string>();
        foreach (string raw in (status ?? string.Empty).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 4 || line[2] != ' ')
                continue;

            string path = line[3..];
            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                path = path[(arrow + 4)..];
            files.Add(path.Trim('"'));
        }

        return [.. files];
    }
}
