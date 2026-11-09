using System.Text.Json.Nodes;

using Horizon.Core.Platform;
using Horizon.UI.Components;

namespace Horizon.UI.Scripting;

public sealed partial class LanguageServer
{
    public async Task<IReadOnlyList<UICodeFoldingRange>?> FoldingRanges(string path, int lineCount)
    {
        var document = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) } };
        if (Offers("foldingRangeProvider"))
        {
            if (await Ask("textDocument/foldingRange", document).ConfigureAwait(false) is not JsonArray ranges) return null;
            return ranges.OfType<JsonObject>().Select(range => new UICodeFoldingRange(JsonNumbers.Int(range["startLine"], -1),
                JsonNumbers.Int(range["endLine"], -1), range["kind"] is JsonValue kind && kind.TryGetValue<string>(out var text) ? text : null))
                .Where(range => range.Start >= 0 && range.End > range.Start && range.End < lineCount).Distinct().OrderBy(range => range.Start).ThenByDescending(range => range.End).ToArray();
        }

        // Pyright provides structural symbol ranges rather than folding ranges. Keep bracket folds alongside them.
        if (LanguageId != "python" || !Offers("documentSymbolProvider")
            || await Ask("textDocument/documentSymbol", document).ConfigureAwait(false) is not JsonArray symbols) return null;
        var found = new List<UICodeFoldingRange>();
        void Read(JsonArray list)
        {
            foreach (JsonObject symbol in list.OfType<JsonObject>())
            {
                JsonObject? location = symbol["location"] as JsonObject;
                bool sameFile = location is null || location["uri"] is JsonValue uri && uri.TryGetValue<string>(out var file)
                    && PathOf(file) is {} local && PlatformPaths.Same(local, path);
                if (sameFile && JsonNumbers.Int(symbol["kind"], 0) is 2 or 3 or 5 or 6 or 9 or 12 or 23
                    && (symbol["range"] ?? location?["range"]) is JsonObject range)
                {
                    int start = JsonNumbers.Int((symbol["selectionRange"] ?? range)?["start"]?["line"], -1);
                    int end = JsonNumbers.Int(range["end"]?["line"], -1);
                    if (JsonNumbers.Int(range["end"]?["character"], -1) == 0) end--;
                    if (start >= 0 && end > start && end < lineCount) found.Add(new(start, end));
                }
                if (symbol["children"] is JsonArray children) Read(children);
            }
        }
        Read(symbols);
        return found.Distinct().OrderBy(range => range.Start).ThenByDescending(range => range.End).ToArray();
    }
}
