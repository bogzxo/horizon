using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public sealed partial class LanguageServer
{
    /// <summary>Clangd's extension for finding the corresponding source or header of an open file.</summary>
    public async Task<string?> SwitchSourceHeader(string path)
    {
        JsonNode? answer = await Ask("textDocument/switchSourceHeader", new JsonObject { ["uri"] = UriOf(path) }).ConfigureAwait(false);
        return answer is JsonValue value && value.TryGetValue<string>(out var uri)
            && Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;
    }
}
