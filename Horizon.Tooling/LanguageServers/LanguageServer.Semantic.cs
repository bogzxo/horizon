using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

// Semantic tokens, what every name in a file is as the server sees it (a class, a field, a parameter), for the editor to
// colour names by what they are rather than by how they look. The whole file is asked for at once, or every line of it
// for a server that only answers a range. A server says the meaning of its numbers once, in the legend of its
// capabilities, and can ask for them to be asked for again (workspace/semanticTokens/refresh), csharp-ls does once its
// project has loaded and the names it couldn't tell apart before can be.
public sealed partial class LanguageServer
{
    // How many times the server has asked for its tokens to be asked for again
    private int semanticRefreshes;

    /// <summary>
    /// Goes up every time the server says what it said of the names is out of date (its project loaded, a file it
    /// depends on changed), for whoever asked to ask again.
    /// </summary>
    public int SemanticRefreshes => Volatile.Read(ref semanticRefreshes);

    // The client's side of it, said as it starts. Both ways of asking, the whole file and a range, never the changes
    private static JsonObject SemanticCapabilities() => new()
    {
        ["dynamicRegistration"] = false,
        ["requests"] = new JsonObject { ["range"] = true, ["full"] = new JsonObject { ["delta"] = false } },
        ["tokenTypes"] = Strings(LanguageServerSemantics.TokenTypes),
        ["tokenModifiers"] = Strings(LanguageServerSemantics.TokenModifiers),
        ["formats"] = new JsonArray("relative"),
        ["overlappingTokenSupport"] = false,
        ["multilineTokenSupport"] = false,
        ["serverCancelSupport"] = false,
        ["augmentsSyntaxTokens"] = true
    };

    private static JsonArray Strings(string[] all)
    {
        var list = new JsonArray();
        foreach (string each in all)
            list.Add((JsonNode?)JsonValue.Create(each));
        return list;
    }

    // The server asked for them to be asked for again
    private void SemanticsRefreshed()
    {
        Interlocked.Increment(ref semanticRefreshes);
        Woken?.Invoke();
    }

    /// <summary>
    /// What the names of a file are, the server's semantic tokens made into spans by its legend. Null for a server that
    /// doesn't do them, or couldn't say, so the editor keeps whatever it had. The file has to be up to date in the server.
    /// </summary>
    /// <param name="lineCount">How many lines the file has, for a server that only answers for a range of them.</param>
    public async Task<IReadOnlyList<SemanticSpan>?> SemanticTokens(string path, int lineCount)
    {
        if (serverCapabilities?["semanticTokensProvider"] is not JsonObject provider || provider["legend"] is not JsonObject legend)
            return null;

        var types = Names(legend["tokenTypes"]);
        var modifiers = Names(legend["tokenModifiers"]);
        if (types.Count == 0)
            return null;

        var document = new JsonObject { ["uri"] = UriOf(path) };
        // full is true or says whether it does deltas, either way the whole file can be asked for
        bool whole = provider["full"] switch
        {
            JsonObject => true,
            JsonValue value when value.TryGetValue(out bool on) => on,
            _ => false
        };

        IReadOnlyList<SemanticSpan>? ParseTokens(ReadOnlyMemory<byte> rawResult)
        {
            if (rawResult.IsEmpty)
                return null;

            var reader = new Utf8JsonReader(rawResult.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                bool isData = reader.ValueTextEquals("data"u8);
                if (!reader.Read())
                    return null;

                if (!isData || reader.TokenType != JsonTokenType.StartArray)
                {
                    if (!reader.TrySkip())
                        return null;
                    continue;
                }

                int[] rented = ArrayPool<int>.Shared.Rent(4096);
                int count = 0;
                try
                {
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        int value;
                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int intValue))
                            value = intValue;
                        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double doubleValue))
                            value = (int)doubleValue;
                        else
                        {
                            if (!reader.TrySkip())
                                return null;
                            value = 0;
                        }

                        if (count == rented.Length)
                        {
                            int[] bigger = ArrayPool<int>.Shared.Rent(rented.Length * 2);
                            Array.Copy(rented, bigger, count);
                            ArrayPool<int>.Shared.Return(rented);
                            rented = bigger;
                        }
                        rented[count++] = value;
                    }

                    return LanguageServerSemantics.Decode(rented.AsSpan(0, count), types, modifiers);
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(rented);
                }
            }

            return null;
        }

        if (whole)
            return await AskParsed("textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = document }, ParseTokens, null).ConfigureAwait(false);

        if (provider["range"] is not null)
            return await AskParsed("textDocument/semanticTokens/range", new JsonObject
            {
                ["textDocument"] = document,
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = Math.Max(0, lineCount), ["character"] = 0 }
                }
            }, ParseTokens, null).ConfigureAwait(false);

        return null;
    }

    /// <summary>The token types and modifiers the server said it sends, its legend, nothing before it's said or for a server that doesn't.</summary>
    public (IReadOnlyList<string> Types, IReadOnlyList<string> Modifiers) SemanticLegend =>
        serverCapabilities?["semanticTokensProvider"]?["legend"] is JsonObject legend ? (Names(legend["tokenTypes"]), Names(legend["tokenModifiers"])) : ([], []);

    // A list of names out of the legend, nothing for anything else
    private static List<string> Names(JsonNode? node)
    {
        var names = new List<string>();
        if (node is JsonArray list)
        {
            foreach (JsonNode? each in list)
                names.Add(each is JsonValue value && value.TryGetValue(out string? name) ? name : string.Empty);
        }

        return names;
    }
}
