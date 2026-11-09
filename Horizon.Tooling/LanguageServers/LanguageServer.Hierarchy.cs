using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>
/// Something a call hierarchy is about, a method say, by its name, what it's in, what kind of symbol it is and where it
/// is declared. The server's own item is kept with it, it's what the server is asked about it with again.
/// </summary>
public sealed record LanguageServerCallItem(string Name, string Detail, int Kind, LanguageServerPlace Declared, JsonObject Item);

/// <summary>
/// One end of some calls, what calls something (or what something calls) and where those calls are. For a caller the
/// places are in the caller's file, for what's called they're in the file of the one doing the calling.
/// </summary>
public readonly record struct LanguageServerCall(LanguageServerCallItem Other, IReadOnlyList<LanguageServerPlace> Sites);

// What implements a name and what calls it, VS's Ctrl+F12 and its call hierarchy. Both are asked of the server the
// same way F12 is, and a server that hasn't got them says nothing
public sealed partial class LanguageServer
{
    /// <summary>Where the name at a place is implemented (an interface's or an abstract member's overrides), nothing for nowhere.</summary>
    public async Task<IReadOnlyList<LanguageServerPlace>> Implementation(string path, int line, int character)
    {
        if (!Offers("implementationProvider"))
            return [];

        return Places(await Ask("textDocument/implementation", At(path, line, character)).ConfigureAwait(false));
    }

    /// <summary>
    /// What the name at a place is to a call hierarchy (prepareCallHierarchy), what its calls are asked about with.
    /// Nothing for a server without a call hierarchy or a place that isn't on something that calls or is called.
    /// </summary>
    public async Task<IReadOnlyList<LanguageServerCallItem>> CallItems(string path, int line, int character)
    {
        if (!Offers("callHierarchyProvider"))
            return [];

        if (await AskCallHierarchy("textDocument/prepareCallHierarchy", At(path, line, character)).ConfigureAwait(false) is not JsonArray items)
            return [];

        return [.. items.Select(CallItemOf).OfType<LanguageServerCallItem>()];
    }

    /// <summary>Everything that calls something (callHierarchy/incomingCalls), each with where its calls are.</summary>
    public async Task<IReadOnlyList<LanguageServerCall>> IncomingCalls(LanguageServerCallItem of) =>
        CallsOf(await AskCallHierarchy("callHierarchy/incomingCalls", new JsonObject { ["item"] = of.Item.DeepClone() }).ConfigureAwait(false), "from", null);

    /// <summary>Everything something calls (callHierarchy/outgoingCalls), each with where in it the calls are.</summary>
    public async Task<IReadOnlyList<LanguageServerCall>> OutgoingCalls(LanguageServerCallItem of) =>
        CallsOf(await AskCallHierarchy("callHierarchy/outgoingCalls", new JsonObject { ["item"] = of.Item.DeepClone() }).ConfigureAwait(false), "to", of.Declared.Path);

    private async Task<JsonNode?> AskCallHierarchy(string method, JsonObject parameters)
    {
        var answer = await Ask(method, parameters, propagateErrors: true).ConfigureAwait(false);
        if (Failure is { } failure) throw new IOException(failure);
        return answer;
    }

    /// <summary>A server's CallHierarchyItem, null for one with no file.</summary>
    public static LanguageServerCallItem? CallItemOf(JsonNode? node)
    {
        if (node is not JsonObject item || item["uri"]?.GetValue<string>() is not { } uri || PathOf(uri) is not { } file)
            return null;

        var (line, character, endLine, endCharacter) = RangeOf((item["selectionRange"] ?? item["range"]) as JsonObject ?? []);
        return new LanguageServerCallItem(item["name"]?.GetValue<string>() ?? "?", item["detail"]?.GetValue<string>() ?? string.Empty,
            JsonNumbers.Int(item["kind"], 0), new LanguageServerPlace(file, line, character, endLine, endCharacter), item);
    }

    /// <summary>
    /// The calls of an incomingCalls or outgoingCalls answer, the other end of each under the name given ("from" or "to").
    /// The places of a call are in the other end's file, or in the file given (the file of the one that's calling).
    /// </summary>
    public static IReadOnlyList<LanguageServerCall> CallsOf(JsonNode? answer, string end, string? sitesIn)
    {
        if (answer is not JsonArray calls)
            return [];

        var read = new List<LanguageServerCall>();
        foreach (JsonNode? call in calls)
        {
            if (CallItemOf(call?[end]) is not { } other)
                continue;

            string file = sitesIn ?? other.Declared.Path;
            var sites = new List<LanguageServerPlace>();
            if (call!["fromRanges"] is JsonArray ranges)
            {
                foreach (JsonNode? range in ranges)
                {
                    if (range is JsonObject at)
                    {
                        var (line, character, endLine, endCharacter) = RangeOf(at);
                        sites.Add(new LanguageServerPlace(file, line, character, endLine, endCharacter));
                    }
                }
            }

            read.Add(new LanguageServerCall(other, sites));
        }

        return read;
    }
}
