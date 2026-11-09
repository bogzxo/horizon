using System.Text;
using System.Text.Json.Nodes;

using Horizon.UI.Components;

namespace Horizon.UI.Scripting;

/// <summary>A place in a file a server pointed at, its start and end, lines and characters from 0.</summary>
public readonly record struct LanguageServerPlace(string Path, int Line, int Character, int EndLine, int EndCharacter)
{
    /// <summary>
    /// Whether it's a file on the disk. One that isn't is the server's own uri (csharp-ls's csharp:/ for a name of the
    /// framework), whose text <see cref="LanguageServer.Source"/> asks for.
    /// </summary>
    public bool OnDisk => !Path.Contains(":/", StringComparison.Ordinal);
}

/// <summary>Something a file declares, its name, what kind of thing it is, more about it, where, and what it declares inside it.</summary>
public sealed record LanguageServerSymbol(string Name, string Kind, string Detail, int Line, int Character, IReadOnlyList<LanguageServerSymbol> Children)
{
    /// <summary>The line the whole of it starts on, its attributes and all, the name's own line when the server doesn't say.</summary>
    public int StartLine { get; init; } = Line;
}

// Asking a server about the code rather than telling it, what a name is (hover), where it's declared and everywhere it's
// used, what a file declares, and what the call the caret is in takes. Each answers null or nothing for a server that
// doesn't do it or couldn't say, and none of them throws, the editor asks and goes on.
public sealed partial class LanguageServer
{
    // What the server said it does as it started, its capabilities, null until it's said
    private JsonObject? serverCapabilities;

    /// <summary>
    /// Whether the server said it does something, by the name of its capability (hoverProvider, definitionProvider,
    /// renameProvider). False until it's started, and for one that said false or nothing.
    /// </summary>
    public bool Offers(string provider) => serverCapabilities?[provider] is { } offered && !(offered is JsonValue value && value.TryGetValue(out bool on) && !on);

    /// <summary>Whether the server asks for completion when this character is typed.</summary>
    public bool TriggersCompletion(char character) => serverCapabilities?["completionProvider"] is JsonObject provider
        && provider["triggerCharacters"] is JsonArray triggers
        && triggers.Any(node => node is JsonValue value && value.TryGetValue<string>(out string? text) && text is { Length: 1 } && text[0] == character);

    // Helper method for the place in a file a question is about
    private JsonObject At(string path, int line, int character) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) },
        ["position"] = new JsonObject { ["line"] = line, ["character"] = character }
    };

    /// <summary>What the name at a place is, as markdown, null for nothing to say.</summary>
    public async Task<string?> Hover(string path, int line, int character)
    {
        if (!Offers("hoverProvider"))
            return null;

        JsonNode? answered = await Ask("textDocument/hover", At(path, line, character)).ConfigureAwait(false);
        return answered is JsonObject hover && Markdown(hover["contents"]) is { Length: > 0 } said ? said : null;
    }

    /// <summary>
    /// What a server said as markdown, whichever way it said it: markup of a kind, a string, a piece of code in a
    /// language, or a list of them, each a paragraph.
    /// </summary>
    public static string Markdown(JsonNode? contents)
    {
        switch (contents)
        {
            case JsonValue text when text.TryGetValue(out string? plain):
                return plain.Trim();
            case JsonObject { } markup when markup["kind"] is not null:
                return markup["value"]?.GetValue<string>()?.Trim() ?? string.Empty;
            case JsonObject { } code when code["language"]?.GetValue<string>() is { } language:
                return $"```{language}\n{code["value"]?.GetValue<string>()?.Trim()}\n```";
            case JsonArray list:
                var all = new StringBuilder();
                foreach (JsonNode? part in list)
                {
                    if (Markdown(part) is { Length: > 0 } said)
                        all.Append(all.Length > 0 ? "\n\n" : string.Empty).Append(said);
                }
                return all.ToString();
            default:
                return string.Empty;
        }
    }

    /// <summary>Where the name at a place is declared, nothing for nowhere the server knows of.</summary>
    public async Task<IReadOnlyList<LanguageServerPlace>> Definition(string path, int line, int character)
    {
        if (!Offers("definitionProvider"))
            return [];

        return Places(await Ask("textDocument/definition", At(path, line, character)).ConfigureAwait(false), anywhere: true);
    }

    /// <summary>
    /// Where the type of the symbol is defined, including server-owned metadata locations.
    /// </summary>
    public async Task<IReadOnlyList<LanguageServerPlace>> TypeDefinition(string path, int line, int character)
    {
        if (!Offers("typeDefinitionProvider")) return [];
        return Places(await Ask("textDocument/typeDefinition", At(path, line, character)).ConfigureAwait(false), anywhere: true);
    }

    /// <summary>The text of a server-owned metadata location, or null when unavailable.</summary>
    public async Task<string?> Source(string uri)
    {
        if (LanguageId != "csharp" || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "csharp") return null;
        string requested = parsed.AbsoluteUri;
        JsonNode? answered = await Ask("csharp/metadata", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = requested } }).ConfigureAwait(false);
        return answered?["source"]?.GetValue<string>();
    }

    /// <summary>Everywhere the name at a place is used, where it's declared among them.</summary>
    public async Task<IReadOnlyList<LanguageServerPlace>> References(string path, int line, int character)
    {
        if (!Offers("referencesProvider"))
            return [];

        JsonObject asked = At(path, line, character);
        asked["context"] = new JsonObject { ["includeDeclaration"] = true };
        return Places(await Ask("textDocument/references", asked).ConfigureAwait(false));
    }

    // Helper method for places however a server says them, one location, a list of them, or links to them. Anywhere keeps a
    // place that isn't a file, by its uri
    private static List<LanguageServerPlace> Places(JsonNode? answered, bool anywhere = false)
    {
        var places = new List<LanguageServerPlace>();
        void Add(JsonNode? node)
        {
            if (node is not JsonObject place)
                return;

            // A link says where it goes and the name's own range in it, a location only where
            string? uri = (place["targetUri"] ?? place["uri"])?.GetValue<string>();
            if (uri is null || (PathOf(uri) ?? (anywhere ? uri : null)) is not { } file || (place["targetSelectionRange"] ?? place["range"]) is not JsonObject range)
                return;

            var (line, character, endLine, endCharacter) = RangeOf(range);
            places.Add(new LanguageServerPlace(file, line, character, endLine, endCharacter));
        }

        if (answered is JsonArray list)
        {
            foreach (JsonNode? each in list)
                Add(each);
        }
        else
        {
            Add(answered);
        }

        return places;
    }

    // Helper method for a range's start and end, lines and characters from 0
    private static (int Line, int Character, int EndLine, int EndCharacter) RangeOf(JsonObject range)
    {
        int line = JsonNumbers.Int(range["start"]?["line"], 0), character = JsonNumbers.Int(range["start"]?["character"], 0);
        return (line, character, JsonNumbers.Int(range["end"]?["line"], line), JsonNumbers.Int(range["end"]?["character"], character));
    }

    // Helper method for the path of a file's uri, null for one that isn't a file on the disk. serve-d writes the drive's
    // colon escaped (file:///C%3A/Users/...), which .NET reads as /C:/Users, a path from the root of the drive Hex is on,
    // and every place it pointed at was C:\C:\Users\..., nowhere
    internal static string? PathOf(string uri)
    {
        try
        {
            var parsed = new Uri(uri);
            if (!parsed.IsFile)
                return null;

            string local = Uri.UnescapeDataString(parsed.LocalPath);
            if (OperatingSystem.IsWindows() && local.Length >= 3 && local[0] is '/' or '\\' && char.IsAsciiLetter(local[1]) && local[2] == ':')
                local = local[1..];
            return Path.GetFullPath(local);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// What a file declares, its types with their members inside them, null for a server that can't say. A server that
    /// says them in a flat list has them put inside what it says they're in.
    /// </summary>
    public async Task<IReadOnlyList<LanguageServerSymbol>?> Symbols(string path)
    {
        if (!Offers("documentSymbolProvider"))
            return null;

        JsonNode? answered = await Ask("textDocument/documentSymbol", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) } }).ConfigureAwait(false);
        if (answered is not JsonArray list)
            return null;

        // The tree of them, DocumentSymbol, has a range of its own and children
        if (list.OfType<JsonObject>().FirstOrDefault() is { } first && first["location"] is null)
            return [.. list.OfType<JsonObject>().Select(SymbolOf)];

        // The flat kind, SymbolInformation, says the name of what it's in. Put inside the last of that name before it
        var top = new List<LanguageServerSymbol>();
        var byName = new Dictionary<string, List<LanguageServerSymbol>>(StringComparer.Ordinal);
        foreach (JsonObject flat in list.OfType<JsonObject>())
        {
            if (flat["location"]?["range"] is not JsonObject range)
                continue;

            var (line, character, _, _) = RangeOf(range);
            var children = new List<LanguageServerSymbol>();
            string name = flat["name"]?.GetValue<string>() ?? string.Empty;
            var symbol = new LanguageServerSymbol(name, SymbolKindOf(JsonNumbers.Int(flat["kind"], 0)), flat["containerName"]?.GetValue<string>() ?? string.Empty, line, character, children);

            if (flat["containerName"]?.GetValue<string>() is { Length: > 0 } container && byName.TryGetValue(container, out var inside))
                inside.Add(symbol);
            else
                top.Add(symbol);
            byName[name] = children;
        }

        return top;
    }

    // Helper method for one of the tree kind, with what's inside it
    private static LanguageServerSymbol SymbolOf(JsonObject symbol)
    {
        var (line, character, _, _) = (symbol["selectionRange"] ?? symbol["range"]) is JsonObject range ? RangeOf(range) : (0, 0, 0, 0);
        int start = symbol["range"] is JsonObject whole ? Math.Min(RangeOf(whole).Line, line) : line;
        return new LanguageServerSymbol(symbol["name"]?.GetValue<string>() ?? string.Empty, SymbolKindOf(JsonNumbers.Int(symbol["kind"], 0)),
            symbol["detail"]?.GetValue<string>() ?? string.Empty, line, character,
            symbol["children"] is JsonArray children ? [.. children.OfType<JsonObject>().Select(SymbolOf)] : []) { StartLine = start };
    }

    /// <summary>What kind of thing a symbol is, by the protocol's numbers, the way a codicon is named (symbol-class).</summary>
    public static string SymbolKindOf(int kind) => kind switch
    {
        1 => "file", 2 => "module", 3 => "namespace", 4 => "package", 5 => "class", 6 => "method", 7 => "property", 8 => "field",
        9 => "constructor", 10 => "enum", 11 => "interface", 12 => "function", 13 => "variable", 14 => "constant",
        15 => "string", 16 => "number", 17 => "boolean", 18 => "array", 19 => "object", 20 => "key", 21 => "null",
        22 => "enum-member", 23 => "struct", 24 => "event", 25 => "operator", 26 => "type-parameter", _ => "misc"
    };

    /// <summary>
    /// What the call at a place takes, its signature with the argument being typed picked out, null for no call there or
    /// a server that can't say. Its documentation goes under it when the server has some.
    /// </summary>
    public async Task<UISignature?> Signature(string path, int line, int character, int fallbackArgument = 0)
    {
        if (!Offers("signatureHelpProvider"))
            return null;

        return SignatureOf(await Ask("textDocument/signatureHelp", At(path, line, character)).ConfigureAwait(false), fallbackArgument);
    }

    /// <summary>
    /// What a signatureHelp answer comes to, the signature the server picked with every overload alongside it (each
    /// with the argument being typed picked out), null for one that says nothing.
    /// </summary>
    public static UISignature? SignatureOf(JsonNode? answer, int fallbackArgument = 0)
    {
        if (answer is not JsonObject help || help["signatures"] is not JsonArray signatures || signatures.Count == 0)
            return null;

        var overloads = new List<UISignature>(signatures.Count);
        int which = Math.Clamp(JsonNumbers.Int(help["activeSignature"], 0), 0, signatures.Count - 1), active = 0;
        for (int i = 0; i < signatures.Count; i++)
        {
            if (signatures[i] is JsonObject signature && OverloadOf(signature, help, fallbackArgument) is { } read)
            {
                if (i == which)
                    active = overloads.Count;
                overloads.Add(read);
            }
            else if (i == which)
            {
                return null;
            }
        }

        return overloads[active] with { Overloads = overloads.Count > 1 ? overloads : null, Active = active };
    }

    // One signature of an answer, null for one with no label
    private static UISignature? OverloadOf(JsonObject signature, JsonObject help, int fallbackArgument)
    {
        if (signature["label"]?.GetValue<string>() is not { Length: > 0 } label)
            return null;

        // The argument from the signature when it says, the help otherwise, and where its name is in the label, by the
        // offsets a server gives or by finding its name
        int argument = JsonNumbers.Int(signature["activeParameter"] ?? help["activeParameter"], fallbackArgument);
        int start = 0, length = 0;
        string parameterDocumentation = string.Empty;
        if (signature["parameters"] is JsonArray parameters && argument >= 0 && argument < parameters.Count)
        {
            parameterDocumentation = PlainDocs(Markdown(parameters[argument]?["documentation"]));
            int searchFrom = label.IndexOf('(') + 1;
            for (int i = 0; i <= argument; i++)
            {
                JsonNode? named = parameters[i]?["label"];
                int at = 0, count = 0;
                if (named is JsonArray offsets && offsets.Count == 2)
                {
                    at = Math.Clamp(JsonNumbers.Int(offsets[0], 0), 0, label.Length);
                    count = Math.Clamp(JsonNumbers.Int(offsets[1], 0) - at, 0, label.Length - at);
                }
                else if (named is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text)
                    && label.IndexOf(text, searchFrom, StringComparison.Ordinal) is var found and >= 0)
                    (at, count) = (found, text.Length);

                // Repeated labels belong to successive parameters, including after an offset-labelled parameter.
                if (count > 0) searchFrom = Math.Max(searchFrom, at + count);
                if (i == argument) (start, length) = (at, count);
            }
        }

        return new UISignature(label, start, length)
        {
            Documentation = PlainDocs(Markdown(signature["documentation"])),
            ParameterDocumentation = parameterDocumentation
        };
    }

    /// <summary>
    /// The inlay hints of some lines of a file (textDocument/inlayHint), the names of parameters and the types of names a
    /// server writes in among the code. Nothing for a server that doesn't do them.
    /// </summary>
    public async Task<IReadOnlyList<UICodeInlay>> InlayHints(string path, int fromLine, int toLine)
    {
        if (!Offers("inlayHintProvider"))
            return [];

        var asked = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) },
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = fromLine, ["character"] = 0 },
                ["end"] = new JsonObject { ["line"] = toLine, ["character"] = 0 }
            }
        };

        var found = new List<UICodeInlay>();
        if (await Ask("textDocument/inlayHint", asked).ConfigureAwait(false) is not JsonArray hints)
            return found;

        foreach (JsonNode? node in hints)
        {
            if (node is not JsonObject hint || hint["position"] is not JsonObject at)
                continue;

            // The label's a string, or parts of one each with a value
            string label = hint["label"] switch
            {
                JsonValue text when text.TryGetValue(out string? plain) => plain,
                JsonArray parts => string.Concat(parts.OfType<JsonObject>().Select(part => part["value"]?.GetValue<string>() ?? string.Empty)),
                _ => string.Empty
            };
            if (label.Trim().Length == 0)
                continue;

            bool padLeft = hint["paddingLeft"] is JsonValue left && left.TryGetValue(out bool l) && l;
            bool padRight = hint["paddingRight"] is JsonValue right && right.TryGetValue(out bool r) && r;
            found.Add(new UICodeInlay(at["line"]?.GetValue<int>() ?? 0, at["character"]?.GetValue<int>() ?? 0, label.Trim(), padLeft, padRight));
        }

        return found;
    }

    private int inlayRefreshes;
    public int InlayRefreshes => Volatile.Read(ref inlayRefreshes);
}
