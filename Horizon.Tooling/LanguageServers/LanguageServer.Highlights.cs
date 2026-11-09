using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public readonly record struct LanguageServerHighlight(int Line, int Column, int EndLine, int EndColumn, int Kind);

public sealed partial class LanguageServer
{
    public async Task<IReadOnlyList<LanguageServerHighlight>> DocumentHighlights(string path, int line, int column)
    {
        if (!Offers("documentHighlightProvider")) return [];
        return HighlightsOf(await Ask("textDocument/documentHighlight", At(path, line, column)).ConfigureAwait(false));
    }

    internal static IReadOnlyList<LanguageServerHighlight> HighlightsOf(JsonNode? response)
    {
        List<LanguageServerHighlight> found = [];
        if (response is not JsonArray items) return found;
        foreach (var item in items)
        {
            if (item?["range"] is not JsonObject range) continue;
            int line = JsonNumbers.Int(range["start"]?["line"], -1), column = JsonNumbers.Int(range["start"]?["character"], -1);
            int endLine = JsonNumbers.Int(range["end"]?["line"], -1), endColumn = JsonNumbers.Int(range["end"]?["character"], -1);
            if (line < 0 || column < 0 || endLine < line || endColumn < 0 || endLine == line && endColumn <= column) continue;
            int kind = JsonNumbers.Int(item?["kind"], 1);
            found.Add(new(line, column, endLine, endColumn, kind is 2 or 3 ? kind : 1));
        }
        return found;
    }
}
