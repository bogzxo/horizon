using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Horizon.UI.Scripting;

public sealed partial class LanguageServer
{
    internal static IReadOnlyList<UICodeDiagnosticLocation>? DiagnosticLocations(JsonNode? value)
    {
        if (value is not JsonArray entries) return null;
        List<UICodeDiagnosticLocation> result = [];
        foreach (var entry in entries)
        {
            if (entry is not JsonObject item || item["location"] is not JsonObject location
                || location["uri"] is not JsonValue address || !address.TryGetValue<string>(out var uri)
                || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile
                || location["range"] is not JsonObject range || range["start"] is not JsonObject start) continue;
            int line = JsonNumbers.Int(start["line"], -1), column = JsonNumbers.Int(start["character"], -1);
            if (line < 0 || column < 0) continue;
            string message = item["message"] is JsonValue text && text.TryGetValue<string>(out var said) ? said : string.Empty;
            var related = new UICodeDiagnosticLocation(parsed.LocalPath, line, column, message);
            if (!result.Contains(related)) result.Add(related);
        }
        return result.Count == 0 ? null : result;
    }
}
