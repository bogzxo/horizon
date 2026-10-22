using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>A change a server wants made to a file's text, the range it replaces (lines and characters from 0) and what goes there.</summary>
public readonly record struct LanguageServerEdit(int Line, int Character, int EndLine, int EndCharacter, string Text);

/// <summary>
/// A fix or a refactoring a server offers at a place, by its title, whether it's the one it would pick, the edits it
/// makes by file, and a command of the server's to run after them (what some fixes are instead of edits).
/// </summary>
public sealed record LanguageServerAction(string Title, bool Preferred, IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> Edits, JsonNode? Command);

// Asking a server to change the code, renaming a name everywhere, formatting a file, and the fixes it offers where
// something's wrong. What comes back is edits by file, which the editor makes (to the file open in it, or on the disk).
// A server can also ask for edits itself (workspace/applyEdit, what a fix that is a command does), they wait in a queue
// for the editor's thread.
public sealed partial class LanguageServer
{
    // Edits the server asked to have made, for the editor's thread to take
    private readonly ConcurrentQueue<IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>>> editsAsked = new();

    // What the server last said is wrong with each file, as it said it, for asking it what fixes there are
    private readonly ConcurrentDictionary<string, JsonArray> rawDiagnostics = new(Horizon.Core.Platform.PlatformPaths.Comparer);

    /// <summary>Takes the next edits the server asked to have made, false when there are none waiting.</summary>
    public bool TryTakeEdits(out IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> edits) => editsAsked.TryDequeue(out edits!);

    /// <summary>Every place a name is used made the new name, by file, null for a server that can't or wouldn't.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>>?> Rename(string path, int line, int character, string name)
    {
        if (!Offers("renameProvider"))
            return null;

        JsonObject asked = At(path, line, character);
        asked["newName"] = name;
        return await Ask("textDocument/rename", asked).ConfigureAwait(false) is JsonObject edit ? WorkspaceEdits(edit) : null;
    }

    /// <summary>The edits that format a whole file the server's way, null for a server that can't.</summary>
    public async Task<IReadOnlyList<LanguageServerEdit>?> Format(string path, int tabSize, bool insertSpaces)
    {
        if (!Offers("documentFormattingProvider"))
            return null;

        JsonNode? answered = await Ask("textDocument/formatting", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) },
            ["options"] = new JsonObject { ["tabSize"] = tabSize, ["insertSpaces"] = insertSpaces }
        }).ConfigureAwait(false);
        return answered is JsonArray edits ? Edits(edits) : null;
    }

    /// <summary>
    /// The fixes and refactorings a server offers for a range of a file, with what it said is wrong there, nothing for
    /// none. The ones it would pick first.
    /// </summary>
    public async Task<IReadOnlyList<LanguageServerAction>> Actions(string path, int line, int character, int endLine, int endCharacter)
    {
        if (!Offers("codeActionProvider"))
            return [];

        // What it said is wrong on those lines, the way it said it, which it goes by for its fixes
        var wrong = new JsonArray();
        if (rawDiagnostics.TryGetValue(Path.GetFullPath(path), out JsonArray? said))
        {
            foreach (JsonObject diagnostic in said.OfType<JsonObject>())
            {
                if (diagnostic["range"] is JsonObject range && RangeOf(range) is var (from, _, to, _) && from <= endLine && to >= line)
                    wrong.Add(diagnostic.DeepClone());
            }
        }

        JsonNode? answered = await Ask("textDocument/codeAction", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) },
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = character },
                ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter }
            },
            ["context"] = new JsonObject { ["diagnostics"] = wrong }
        }).ConfigureAwait(false);

        if (answered is not JsonArray offered)
            return [];

        var actions = new List<LanguageServerAction>();
        foreach (JsonObject action in offered.OfType<JsonObject>())
        {
            if (action["title"]?.GetValue<string>() is not { Length: > 0 } title || action["disabled"] is not null)
                continue;

            // A command on its own is a command, a code action has edits, a command or both
            bool bare = action["command"] is JsonValue;
            IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> edits = action["edit"] is JsonObject edit ? WorkspaceEdits(edit) : new Dictionary<string, IReadOnlyList<LanguageServerEdit>>();
            JsonNode? command = bare ? action.DeepClone() : action["command"]?.DeepClone();
            bool preferred = action["isPreferred"] is JsonValue flag && flag.TryGetValue(out bool yes) && yes;
            actions.Add(new LanguageServerAction(title, preferred, edits, command));
        }

        return [.. actions.OrderByDescending(action => action.Preferred)];
    }

    /// <summary>Runs a command of the server's (a fix's), which may ask for edits back (see <see cref="TryTakeEdits"/>).</summary>
    public async Task Execute(JsonNode command)
    {
        if (command["command"]?.GetValue<string>() is not { Length: > 0 } name)
            return;

        var asked = new JsonObject { ["command"] = name };
        if (command["arguments"] is JsonArray arguments)
            asked["arguments"] = arguments.DeepClone();
        await Ask("workspace/executeCommand", asked).ConfigureAwait(false);
    }

    // Helper method for a workspace edit's edits by file, from its changes or its document changes
    private static Dictionary<string, IReadOnlyList<LanguageServerEdit>> WorkspaceEdits(JsonObject edit)
    {
        var byFile = new Dictionary<string, IReadOnlyList<LanguageServerEdit>>(Horizon.Core.Platform.PlatformPaths.Comparer);
        if (edit["changes"] is JsonObject changes)
        {
            foreach (var (uri, edits) in changes)
            {
                if (PathOf(uri) is { } file && edits is JsonArray list)
                    byFile[file] = Edits(list);
            }
        }

        if (edit["documentChanges"] is JsonArray documents)
        {
            foreach (JsonObject document in documents.OfType<JsonObject>())
            {
                if (document["textDocument"]?["uri"]?.GetValue<string>() is { } uri && PathOf(uri) is { } file && document["edits"] is JsonArray list)
                    byFile[file] = [.. byFile.GetValueOrDefault(file) ?? [], .. Edits(list)];
            }
        }

        return byFile;
    }

    // Helper method for a list of text edits
    private static List<LanguageServerEdit> Edits(JsonArray list)
    {
        var edits = new List<LanguageServerEdit>();
        foreach (JsonObject edit in list.OfType<JsonObject>())
        {
            if (edit["range"] is not JsonObject range)
                continue;

            var (line, character, endLine, endCharacter) = RangeOf(range);
            edits.Add(new LanguageServerEdit(line, character, endLine, endCharacter, edit["newText"]?.GetValue<string>() ?? string.Empty));
        }

        return edits;
    }

    /// <summary>
    /// Text with edits made to it, the way the protocol means them, every range in the text as it was before any of
    /// them. Lines and characters past the end are held to it.
    /// </summary>
    public static string Apply(string text, IReadOnlyList<LanguageServerEdit> edits)
    {
        // Where each line starts in the text as it was
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }

        int OffsetOf(int line, int character)
        {
            if (line >= starts.Count)
                return text.Length;
            int start = starts[Math.Max(0, line)];
            int end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
            if (end > start && text[end - 1] == '\r')
                end--;
            return Math.Min(start + Math.Max(0, character), end);
        }

        // From the last to the first, so each edit's offsets are still the text's as it was
        var sorted = edits.Select((edit, order) => (From: OffsetOf(edit.Line, edit.Character), To: OffsetOf(edit.EndLine, edit.EndCharacter), edit.Text, order))
            .OrderByDescending(edit => edit.From).ThenByDescending(edit => edit.order);
        var result = new System.Text.StringBuilder(text);
        foreach (var (from, to, newText, _) in sorted)
        {
            int end = Math.Max(from, to);
            result.Remove(from, end - from).Insert(from, newText);
        }

        return result.ToString();
    }

    // The server asking for edits (workspace/applyEdit), queued for the editor and answered that they were made
    private JsonNode? ApplyAsked(JsonNode? parameters)
    {
        if (parameters?["edit"] is not JsonObject edit)
            return new JsonObject { ["applied"] = false };

        editsAsked.Enqueue(WorkspaceEdits(edit));
        Woken?.Invoke();
        return new JsonObject { ["applied"] = true };
    }

    // What was said wrong with a file, kept as the server said it for its fixes
    private void KeepDiagnostics(string path, JsonArray? diagnostics) => rawDiagnostics[Path.GetFullPath(path)] = (JsonArray?)diagnostics?.DeepClone() ?? [];
}
