using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public sealed record LanguageServerTypeItem(string Name, string Detail, int Kind, LanguageServerPlace Declared, JsonObject Item);

public sealed partial class LanguageServer
{
    public async Task<IReadOnlyList<LanguageServerTypeItem>> TypeItems(string path, int line, int character)
    {
        if (!Offers("typeHierarchyProvider")) return [];
        var answer = await Ask("textDocument/prepareTypeHierarchy", At(path, line, character), propagateErrors: true).ConfigureAwait(false);
        if (Failure is { } failure) throw new IOException(failure);
        return TypeItemsOf(answer);
    }

    public async Task<IReadOnlyList<LanguageServerTypeItem>> RelatedTypes(LanguageServerTypeItem item, bool bases)
    {
        if (!Offers("typeHierarchyProvider")) return [];
        var answer = await Ask(bases ? "typeHierarchy/supertypes" : "typeHierarchy/subtypes",
            new JsonObject { ["item"] = item.Item.DeepClone() }, propagateErrors: true).ConfigureAwait(false);
        if (Failure is { } failure) throw new IOException(failure);
        return TypeItemsOf(answer);
    }

    private static IReadOnlyList<LanguageServerTypeItem> TypeItemsOf(JsonNode? answer)
    {
        if (answer is not JsonArray items) return [];
        // Both LSP hierarchy item shapes share name, kind, uri, range and selectionRange.
        return [.. items.Select(CallItemOf).OfType<LanguageServerCallItem>()
            .Select(item => new LanguageServerTypeItem(item.Name, item.Detail, item.Kind, item.Declared, item.Item))];
    }
}
