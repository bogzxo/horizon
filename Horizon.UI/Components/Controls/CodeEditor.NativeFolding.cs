namespace Horizon.UI.Components;

/// <summary>A whole-line fold; the start stays visible and the following lines through the end are hidden.</summary>
public readonly record struct UICodeFoldingRange(int Start, int End, string? Kind = null);

public partial class CodeEditor
{
    private IReadOnlyList<UICodeFoldingRange>? nativeFoldingRanges;
    private bool completeNativeFolding;

    public IReadOnlyList<UICodeFoldingRange>? NativeFoldingRanges => nativeFoldingRanges;

    /// <summary>Applies current server ranges, optionally supplementing bracket folds with symbol ranges.</summary>
    public void SetNativeFoldingRanges(IReadOnlyList<UICodeFoldingRange>? ranges, bool supplement = false)
    {
        nativeFoldingRanges = ranges?.Where(range => range.Start >= 0 && range.End > range.Start && range.End < document.LineCount).ToArray();
        completeNativeFolding = !supplement;
        FindFoldable();
        foldsChanged++;
    }
}
