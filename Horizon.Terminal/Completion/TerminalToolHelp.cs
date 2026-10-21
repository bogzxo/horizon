using System.Collections.Concurrent;

namespace Horizon.UI.Components;

/// <summary>
/// What every tool said it takes, learned the first time a word of it is completed and kept for the run, for every
/// terminal. A topic is a tool and the subcommands that lead to it ("git stash"), and asking for one that isn't known
/// yet starts finding out and hands back nothing for now, <see cref="Version"/> goes up when the answer is in so the
/// list is worked out again. Git is asked with -h, git's --help opens the manual in a browser.
/// </summary>
public sealed class TerminalToolHelp
{
    /// <summary>The one every terminal shares, a tool's help is the same in all of them.</summary>
    public static TerminalToolHelp Shared { get; } = new();

    // A subcommand's subcommands are asked about, theirs aren't, nothing goes that deep that's worth the processes
    public const int DEEPEST = 2;

    private readonly ConcurrentDictionary<string, TerminalHelpTopic?> topics = new(StringComparer.OrdinalIgnoreCase);
    private int version;

    /// <summary>Goes up every time a topic is learned.</summary>
    public int Version => Volatile.Read(ref version);

    /// <summary>What a tool, or a subcommand of it, takes. Null while it's being found out, empty when it said nothing that reads.</summary>
    public TerminalHelpTopic? Topic(string tool, IReadOnlyList<string> path)
    {
        if (path.Count > DEEPEST)
            return TerminalHelpTopic.Empty;

        // By the tool's name, git and C:\Program Files\Git\cmd\git.exe are the one git, and the name is what's run
        string name = ToolName(tool);
        string key = KeyOf(name, path);
        if (topics.TryGetValue(key, out TerminalHelpTopic? known))
            return known;

        // Null marks it as on its way, the first to ask starts it and everybody after waits for the version to go up
        if (topics.TryAdd(key, null))
            _ = Learn(key, name, [.. path]);
        return null;
    }

    /// <summary>A topic said by hand rather than learned, for a test.</summary>
    public void Teach(string tool, IReadOnlyList<string> path, TerminalHelpTopic topic)
    {
        topics[KeyOf(ToolName(tool), path)] = topic;
        Interlocked.Increment(ref version);
    }

    private static string KeyOf(string name, IReadOnlyList<string> path) => name + " " + string.Join(' ', path);

    private async Task Learn(string key, string tool, string[] path)
    {
        TerminalHelpTopic topic = TerminalHelpTopic.Empty;
        try
        {
            bool git = IsGit(tool);
            string[] arguments = path.Length == 0 && git ? ["help", "-a"] : [.. path, git ? "-h" : "--help"];
            string? said = await TerminalToolRunner.RunAsync(tool, arguments, null).ConfigureAwait(false);
            if (said is not null)
                topic = TerminalHelpParser.Parse(said, string.Join(' ', [ToolName(tool), .. path]));
        }
        catch (Exception)
        {
            // a tool that falls over asked for its help has none, as far as anybody can tell
        }

        topics[key] = topic;
        Interlocked.Increment(ref version);
    }

    /// <summary>The tool's name without a path or an extension, git for C:\Program Files\Git\cmd\git.exe.</summary>
    public static string ToolName(string tool) => Path.GetFileNameWithoutExtension(tool).ToLowerInvariant();

    public static bool IsGit(string tool) => ToolName(tool) == "git";
}
