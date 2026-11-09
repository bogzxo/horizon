using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public sealed partial class LanguageServer
{
    public bool TriggersFormatting(char character)
    {
        if (serverCapabilities?["documentOnTypeFormattingProvider"] is not JsonObject provider) return false;
        if (IsFormattingTrigger(provider["firstTriggerCharacter"], character)) return true;
        if (provider["moreTriggerCharacter"] is JsonArray more)
            foreach (JsonNode? value in more)
                if (IsFormattingTrigger(value, character)) return true;
        return false;
    }

    private static bool IsFormattingTrigger(JsonNode? value, char character) => value is JsonValue item && item.TryGetValue<string>(out var text)
        && text.Length == 1 && text[0] == character;

    public async Task<IReadOnlyList<LanguageServerEdit>?> FormatOnType(string path, int line, int character, char typed, int tabSize, bool insertSpaces)
    {
        if (!TriggersFormatting(typed)) return null;
        var parameters = At(path, line, character);
        parameters["ch"] = typed.ToString();
        parameters["options"] = new JsonObject { ["tabSize"] = tabSize, ["insertSpaces"] = insertSpaces };
        return await Ask("textDocument/onTypeFormatting", parameters).ConfigureAwait(false) is JsonArray edits ? Edits(edits) : null;
    }
}
