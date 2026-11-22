using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Horizon.UI.Scripting;

// The files a server asked to be told of (client/registerCapability for workspace/didChangeWatchedFiles), which is all
// of what changes on the disk that it's told (FilesChanged). One that never asked is told of everything, as before.
// csharp-ls asks for **/*.{cs,cshtml,csproj,sln,slnx}, and was told of every chat Hex saved as it streamed.
public sealed partial class LanguageServer
{
    private sealed record Watcher(string Id, Regex Glob, string? Under, int Kinds);

    private readonly Lock watching = new();
    private readonly List<Watcher> watchers = [];

    /// <summary>Whether the server said which files it wants to be told of, rather than being told of all of them.</summary>
    public bool WatchesFiles
    {
        get
        {
            lock (watching)
                return watchers.Count > 0;
        }
    }

    /// <summary>
    /// Whether a change to a file is one the server asked to be told of. True for every file when it asked for none,
    /// and for a server that hasn't said yet.
    /// </summary>
    public bool Watches(string path, LanguageServerFileChange change)
    {
        lock (watching)
        {
            if (watchers.Count == 0)
                return true;

            string full = Path.GetFullPath(path).Replace('\\', '/');
            int kind = change switch { LanguageServerFileChange.Created => 1, LanguageServerFileChange.Changed => 2, _ => 4 };
            foreach (Watcher watcher in watchers)
            {
                if ((watcher.Kinds & kind) == 0)
                    continue;

                // A pattern under a folder of its own goes by the path from there, a bare one by the whole path
                string against = full;
                if (watcher.Under is { } under)
                {
                    if (!full.StartsWith(under, StringComparison.OrdinalIgnoreCase))
                        continue;
                    against = full[under.Length..];
                }
                if (watcher.Glob.IsMatch(against))
                    return true;
            }
            return false;
        }
    }

    // The server registering what it wants to be told of. Anything but files watched is answered with nothing as before
    private void Registered(JsonNode? parameters)
    {
        if (parameters?["registrations"] is not JsonArray registrations)
            return;

        foreach (JsonNode? registration in registrations)
        {
            if (registration?["method"]?.GetValue<string>() != "workspace/didChangeWatchedFiles"
                || registration["registerOptions"]?["watchers"] is not JsonArray list)
                continue;

            string id = registration["id"]?.GetValue<string>() ?? string.Empty;
            foreach (JsonNode? each in list)
            {
                if (WatcherOf(id, each) is { } watcher)
                {
                    lock (watching)
                        watchers.Add(watcher);
                }
            }
        }
    }

    private void Unregistered(JsonNode? parameters)
    {
        // The protocol spells it unregisterations
        if ((parameters?["unregisterations"] ?? parameters?["unregistrations"]) is not JsonArray list)
            return;

        foreach (JsonNode? each in list)
        {
            if (each?["method"]?.GetValue<string>() != "workspace/didChangeWatchedFiles" || each["id"]?.GetValue<string>() is not { } id)
                continue;
            lock (watching)
                watchers.RemoveAll(watcher => watcher.Id == id);
        }
    }

    // Helper method to read one watcher, a glob or a glob under a folder (a RelativePattern), null for one that can't be read
    private static Watcher? WatcherOf(string id, JsonNode? watcher)
    {
        JsonNode? glob = watcher?["globPattern"];
        int kinds = JsonNumbers.Int(watcher?["kind"], 7);
        string? pattern = null, under = null;
        if (glob is JsonValue value && value.TryGetValue(out string? text))
            pattern = text;
        else if (glob is JsonObject relative && relative["pattern"]?.GetValue<string>() is { } inside)
        {
            pattern = inside;
            string? baseUri = relative["baseUri"] is JsonValue uri && uri.TryGetValue(out string? said) ? said : relative["baseUri"]?["uri"]?.GetValue<string>();
            if (baseUri is null || !Uri.TryCreate(baseUri, UriKind.Absolute, out Uri? folder) || !folder.IsFile)
                return null;
            under = Path.GetFullPath(folder.LocalPath).Replace('\\', '/').TrimEnd('/') + "/";
        }

        if (string.IsNullOrEmpty(pattern))
            return null;
        try
        {
            return new Watcher(id, GlobRegex(pattern, under is null), under, kinds);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// An LSP glob as a regular expression over a path with forward slashes, * within a folder, ** across folders, ?
    /// one character, {a,b} either, [a-z] a range. A bare pattern not starting at the root (*.cs) matches at the end of
    /// a whole path, the way editors take it. Case is ignored, Windows' paths are.
    /// </summary>
    internal static Regex GlobRegex(string glob, bool anywhere)
    {
        var regex = new StringBuilder(anywhere && !glob.StartsWith("**/", StringComparison.Ordinal) && !Path.IsPathRooted(glob) ? "(^|/)" : "^");
        int braces = 0;
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    // **/ is any folders or none, ** at the end anything
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        regex.Append("(.*/)?");
                    }
                    else
                        regex.Append(".*");
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '{':
                    braces++;
                    regex.Append("(?:");
                    break;
                case '}' when braces > 0:
                    braces--;
                    regex.Append(')');
                    break;
                case ',' when braces > 0:
                    regex.Append('|');
                    break;
                case '[':
                    int end = glob.IndexOf(']', i + 1);
                    if (end < 0)
                        goto default;
                    string inside = glob[(i + 1)..end];
                    regex.Append('[').Append(inside.StartsWith('!') ? "^" + Regex.Escape(inside[1..]).Replace("\\-", "-") : Regex.Escape(inside).Replace("\\-", "-")).Append(']');
                    i = end;
                    break;
                case '\\':
                    regex.Append('/');
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return new Regex(regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
