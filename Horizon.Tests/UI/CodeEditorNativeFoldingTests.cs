using Horizon.UI.Components;

namespace Horizon.Tests.UI;

public class CodeEditorNativeFoldingTests
{
    [Fact]
    public void RegionsFoldAndAnAuthoritativeEmptyResponseRemovesThem()
    {
        var editor = new CodeEditor("region\none\ntwo\nend\n");
        editor.SetNativeFoldingRanges([new(0, 3, "region")]);
        Assert.True(editor.CanFold(0));
        editor.ToggleFold(0);
        Assert.Contains(0, editor.FoldedLines);
        editor.SetNativeFoldingRanges([]);
        Assert.False(editor.CanFold(0));
        Assert.Empty(editor.FoldedLines);
        Assert.False(editor.Document.CanUndo);
    }

    [Fact]
    public void InvalidRangesAreIgnoredAndTextChangesRetireTheSnapshot()
    {
        var editor = new CodeEditor("one\ntwo\nthree\n");
        editor.SetNativeFoldingRanges([new(-1, 2), new(1, 1), new(0, 8), new(0, 2)]);
        Assert.Equal(new UICodeFoldingRange(0, 2), Assert.Single(editor.NativeFoldingRanges!));
        editor.Document.Type('x');
        Assert.Null(editor.NativeFoldingRanges);
        editor.SetNativeFoldingRanges([]);
        Assert.False(editor.CanFold(0));
    }

    [Fact]
    public void SymbolRangesSupplementBracketsAndCompleteRangesReplaceThem()
    {
        var editor = new CodeEditor("{\n value: 1\n}\ndefinition\n body\nend\n");
        typeof(CodeEditor).GetMethod("Recolour", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, null);
        Assert.True(editor.CanFold(0));
        editor.SetNativeFoldingRanges([new(3, 5)], supplement: true);
        Assert.True(editor.CanFold(0));
        Assert.True(editor.CanFold(3));
        editor.SetNativeFoldingRanges([new(3, 5)]);
        Assert.False(editor.CanFold(0));
        editor.SetNativeFoldingRanges(null);
        Assert.True(editor.CanFold(0));
    }

    [Fact]
    public void FoldLevelsUseNativeNestingWithoutBraceDepth()
    {
        var editor = new CodeEditor("class\n method\n  nested\n   body\n  end\n method end\nclass end\n");
        editor.SetNativeFoldingRanges([new(0, 6), new(1, 5), new(2, 4)]);
        editor.FoldAll(2);
        Assert.DoesNotContain(0, editor.FoldedLines);
        Assert.Contains(1, editor.FoldedLines);
        Assert.Contains(2, editor.FoldedLines);
    }

    [Fact]
    public void ShrinkingADocumentNeverQueriesOutOfBoundsFoldHeaders()
    {
        var editor = new CodeEditor("first\nsecond\nthird\nfourth\nfifth\n");
        editor.SetNativeFoldingRanges([new(0, 4), new(2, 4)]);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(CodeEditor).GetMethod("UpdateFolds", flags)!.Invoke(editor, null);
        typeof(CodeEditor).GetMethod("StickyLines", flags)!.Invoke(editor, [3]);
        editor.Document.SelectAll();
        editor.Document.Insert("short\n");
        // Sticky queries can occur before the next editor update as well as during layout.
        var sticky = Assert.IsType<List<int>>(typeof(CodeEditor).GetMethod("StickyLines", flags)!.Invoke(editor, [3]));
        Assert.All(sticky, line => Assert.True(line < editor.Document.LineCount));
        typeof(CodeEditor).GetMethod("UpdateFolds", flags)!.Invoke(editor, null);
        Assert.False(editor.CanFold(2));
        typeof(CodeEditor).GetMethod("StickyLines", flags)!.Invoke(editor, [1]);
    }
}
