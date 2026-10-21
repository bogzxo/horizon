namespace Horizon.UI.Components;

/// <summary>
/// Completion for the words after a tool, its subcommands and its flags out of its own help (<see cref="TerminalToolHelp"/>),
/// for every tool on the list of the ones that are fine to ask (<see cref="DefaultTools"/>, the completion_tools setting).
/// A tool off the list is never run, a script that doesn't know --help would do whatever it does. Git gets what no help
/// says on top, its branches, tags, remotes and changed files where those go (<see cref="TerminalGitValues"/>).
/// </summary>
public sealed class TerminalToolCompletion
{
    /// <summary>The tools that are asked for their help unless the settings say otherwise, well known ones whose --help only prints.</summary>
    public static readonly string[] DefaultTools =
    [
        "git", "dotnet", "npm", "pnpm", "yarn", "cargo", "rustup", "go", "docker", "kubectl", "gh", "pip", "winget",
        "choco", "scoop", "az", "helm", "terraform", "deno", "bun", "node", "python", "py"
    ];

    // Git's subcommands that take a branch or a tag, the ones that take a file with changes, and the ones whose first
    // word is a remote and second a branch
    private static readonly HashSet<string> GitRefs = new(StringComparer.Ordinal)
    {
        "checkout", "switch", "merge", "rebase", "branch", "log", "diff", "cherry-pick", "reset", "show", "revert", "tag",
        "rev-parse", "rev-list", "range-diff", "worktree", "bisect", "blame", "describe", "format-patch", "shortlog"
    };

    private static readonly HashSet<string> GitFiles = new(StringComparer.Ordinal) { "add", "restore", "rm", "mv", "diff", "checkout", "stage", "commit", "blame", "difftool" };
    private static readonly HashSet<string> GitRemotes = new(StringComparer.Ordinal) { "push", "pull", "fetch" };

    private readonly TerminalToolHelp help;
    private readonly TerminalGitValues git;
    private readonly HashSet<string> tools;

    public TerminalToolCompletion(IEnumerable<string>? tools = null, TerminalToolHelp? help = null, TerminalGitValues? git = null)
    {
        this.tools = new HashSet<string>((tools ?? DefaultTools).Select(TerminalToolHelp.ToolName), StringComparer.OrdinalIgnoreCase);
        this.help = help ?? TerminalToolHelp.Shared;
        this.git = git ?? TerminalGitValues.Shared;
    }

    /// <summary>Goes up when something was learned that could change a list.</summary>
    public int Version => help.Version + git.Version;

    /// <summary>
    /// What fits the word being typed after a tool, best first, null when the command isn't a tool on the list (so the
    /// files and commands are offered as they always were).
    /// </summary>
    /// <param name="words">The command's words before the one being typed, the tool first.</param>
    public List<TerminalLineCompletion>? Complete(IReadOnlyList<string> words, string word, string? folder)
    {
        if (words.Count == 0 || !tools.Contains(TerminalToolHelp.ToolName(words[0])))
            return null;

        string tool = words[0];
        bool isGit = TerminalToolHelp.IsGit(tool);

        // Down the subcommands that were typed, as far as they are subcommands, the rest are what they're given
        var path = new List<string>();
        var given = new List<string>();
        TerminalHelpTopic? topic = help.Topic(tool, path);
        // The nearest topic up the path that has flags, git stash's help has every one of its subcommands' flags in it
        TerminalHelpTopic? flagged = topic is { Flags.Count: > 0 } ? topic : null;
        for (int i = 1; i < words.Count; i++)
        {
            string typed = words[i];
            if (typed.StartsWith('-'))
                continue;

            if (given.Count == 0 && topic is not null && path.Count < TerminalToolHelp.DEEPEST && topic.HasSubcommand(typed))
            {
                path.Add(typed);
                topic = help.Topic(tool, path);
                if (topic is { Flags.Count: > 0 })
                    flagged = topic;
                continue;
            }

            given.Add(typed);
        }

        var found = new List<TerminalLineCompletion>();
        if (word.StartsWith('-'))
        {
            foreach (TerminalHelpItem flag in (topic is { Flags.Count: > 0 } ? topic : flagged)?.Flags ?? [])
            {
                if (flag.Name.StartsWith(word, StringComparison.Ordinal) && flag.Name != word)
                    found.Add(new TerminalLineCompletion(flag.Name, false, false, flag.Note));
            }

            return found;
        }

        if (given.Count == 0 && topic is not null)
        {
            foreach (TerminalHelpItem sub in topic.Subcommands)
            {
                if (sub.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase) && !sub.Name.Equals(word, StringComparison.OrdinalIgnoreCase))
                    found.Add(new TerminalLineCompletion(sub.Name, false, true, sub.Note));
            }
        }

        if (isGit && path.Count > 0)
            AddGitValues(path, given.Count, word, folder, found);

        return found;
    }

    private void AddGitValues(List<string> path, int position, string word, string? folder, List<TerminalLineCompletion> found)
    {
        if (git.Of(folder) is not { } repository)
            return;

        string subcommand = path[0];
        IEnumerable<string> values = [];
        if (GitRemotes.Contains(subcommand))
            values = position == 0 ? repository.Remotes : repository.Refs.Where(name => !name.Contains('/'));
        // git remote remove origin, git remote rename origin upstream
        else if (subcommand == "remote" && (path.Count > 1 || position >= 1))
            values = repository.Remotes;
        else
        {
            if (GitFiles.Contains(subcommand))
                values = repository.Changed;
            if (GitRefs.Contains(subcommand))
                values = values.Concat(repository.Refs);
        }

        var seen = new HashSet<string>(found.Select(item => item.Text), StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (value.StartsWith(word, StringComparison.OrdinalIgnoreCase) && value != word && seen.Add(value))
                found.Add(new TerminalLineCompletion(value, false, false, repository.Changed.Contains(value) ? "changed" : null));
        }
    }
}
