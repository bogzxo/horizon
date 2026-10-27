using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Horizon.UI.Scripting;

/// <summary>
/// A code lens a server says is in a file, where it goes and what it says. Title is empty until it's resolved, and the
/// lens as the server said it is kept to hand back when it's resolved.
/// </summary>
public sealed record LanguageServerLens(int Line, int Character, string Title, JsonNode? Said);

// Code lenses, the little words a server puts over a declaration (csharp-ls says how many places use it). Asked for the
// whole file, each said with nothing in it yet and resolved on its own when it comes into sight, a server works out a
// count only when it's asked for that one.
public sealed partial class LanguageServer
{
    /// <summary>The lenses of a file, most of them still to be resolved. Null for a server that doesn't do them.</summary>
    public async Task<IReadOnlyList<LanguageServerLens>?> CodeLenses(string path)
    {
        if (!Offers("codeLensProvider"))
            return null;

        JsonNode? answered = await Ask("textDocument/codeLens", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) } }).ConfigureAwait(false);
        if (answered is not JsonArray list)
            return [];

        var lenses = new List<LanguageServerLens>(list.Count);
        foreach (JsonObject lens in list.OfType<JsonObject>())
        {
            if (lens["range"] is not JsonObject range)
                continue;

            var (line, character, _, _) = RangeOf(range);
            lenses.Add(new LanguageServerLens(line, character, lens["command"]?["title"]?.GetValue<string>() ?? string.Empty, lens.DeepClone()));
        }

        return lenses;
    }

    /// <summary>What a lens says once the server's worked it out, the title it had if it had one already, null when it couldn't say.</summary>
    public async Task<string?> ResolveLens(LanguageServerLens lens)
    {
        if (lens.Title.Length > 0)
            return lens.Title;
        if (lens.Said is null || serverCapabilities?["codeLensProvider"]?["resolveProvider"] is not JsonValue resolves || !resolves.TryGetValue(out bool does) || !does)
            return null;

        JsonNode? answered = await Ask("codeLens/resolve", lens.Said.DeepClone()).ConfigureAwait(false);
        return answered?["command"]?["title"]?.GetValue<string>();
    }

    /// <summary>
    /// How many places a lens says use what it's over, out of "3 references", "1 Reference(s)" or "12 usages". Null for a
    /// lens that says something else (Run Test, say).
    /// </summary>
    public static int? ReferenceCountOf(string title)
    {
        Match found = CountWords().Match(title);
        return found.Success && int.TryParse(found.Groups[1].ValueSpan, out int count) ? count : null;
    }

    [GeneratedRegex(@"(\d+)\s*(?:references?|usages?)", RegexOptions.IgnoreCase)]
    private static partial Regex CountWords();
}
