namespace Horizon.UI.Components;

/// <summary>Deferred display information, without changing what accepting a completion edits.</summary>
public readonly record struct UICompletionDocumentation(string Detail, string Documentation);

public partial class CodeEditor
{
    private Task<UICompletionDocumentation?>? completionDocumentation;
    private Func<Task<UICompletionDocumentation?>>? completionDocumentationFor;

    private void UpdateCompletionDocumentation()
    {
        if (!CompletionOpen || completionIndex < 0 || completionIndex >= completions.Count)
            return;
        UICompletion picked = completions[completionIndex];
        if (picked.ResolveDocumentation is not { } resolve)
            return;
        if (!ReferenceEquals(completionDocumentationFor, resolve))
        {
            completionDocumentationFor = resolve;
            completionDocumentation = null;
            try { completionDocumentation = resolve(); }
            catch { return; } // Documentation failure must not prevent accepting the suggestion.
        }
        if (completionDocumentation is { IsCompletedSuccessfully: true } done && done.Result is { } found)
        {
            // Only the currently highlighted item's task is observed. A late response for a previous
            // selection or list cannot replace another item's documentation or its edit metadata.
            completions[completionIndex] = picked with
            {
                Detail = found.Detail.Length > 0 ? found.Detail : picked.Detail,
                Documentation = found.Documentation.Length > 0 ? found.Documentation : picked.Documentation,
                ResolveDocumentation = null
            };
        }
    }
}
