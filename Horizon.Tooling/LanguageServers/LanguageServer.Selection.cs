using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Horizon.UI.Scripting;

public readonly record struct LanguageServerSelection(UITextPosition Start, UITextPosition End);

public sealed partial class LanguageServer
{
    public async Task<IReadOnlyList<LanguageServerSelection>> SelectionRanges(string path, int line, int column)
    {
        if (!Offers("selectionRangeProvider")) return [];
        var query = new JsonObject {
            ["textDocument"] = new JsonObject { ["uri"] = UriOf(path) },
            ["positions"] = new JsonArray(new JsonObject { ["line"] = line, ["character"] = column }) };
        return SelectionRangesOf(await Ask("textDocument/selectionRange", query).ConfigureAwait(false));
    }

    internal static IReadOnlyList<LanguageServerSelection> SelectionRangesOf(JsonNode? response)
    {
        List<LanguageServerSelection> ranges = [];
        JsonNode? node = response is JsonArray { Count: > 0 } array ? array[0] : null;
        for (int count = 0; node is not null && count < 256; count++, node = node["parent"])
        {
            if (node["range"] is not JsonObject range) break;
            var start = new UITextPosition(JsonNumbers.Int(range["start"]?["line"], -1), JsonNumbers.Int(range["start"]?["character"], -1));
            var end = new UITextPosition(JsonNumbers.Int(range["end"]?["line"], -1), JsonNumbers.Int(range["end"]?["character"], -1));
            if (start.Line < 0 || start.Column < 0 || end.Line < 0 || end.Column < 0 || end < start) break;
            var item = new LanguageServerSelection(start, end);
            if (ranges.Count > 0)
            {
                if (start > ranges[^1].Start || end < ranges[^1].End) break;
                if (item == ranges[^1]) continue;
            }
            ranges.Add(item);
        }
        return ranges;
    }
}
