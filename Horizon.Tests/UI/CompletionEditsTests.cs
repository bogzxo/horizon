using Horizon.UI.Components;

namespace Horizon.Tests;

public class CompletionEditsTests
{
    [Fact]
    public void ImportsAndCompletionAreOneChangeWithCaretAndLineHistory()
    {
        var doc = new UITextDocument { Text = "value = ans\nnext" };
        doc.MoveTo(new(0, 11));
        int changes = 0;
        List<int> shifts = [];
        doc.Changed += () => changes++;
        doc.LinesShifted += (_, by, _) => shifts.Add(by);
        Assert.True(doc.ApplyCompletion(new(new(0, 8), new(0, 11), "answer"),
            [new(new(0, 0), new(0, 0), "from helper import answer\r\n\r\n")]));
        Assert.Equal("from helper import answer\n\nvalue = answer\nnext", doc.Text);
        Assert.Equal(new UITextPosition(2, 14), doc.Caret);
        Assert.Equal(1, changes);
        Assert.Equal(2, Assert.Single(shifts));
        doc.Undo();
        Assert.Equal("value = ans\nnext", doc.Text);
        Assert.Equal(new UITextPosition(0, 11), doc.Caret);
        Assert.False(doc.CanUndo);
        Assert.Equal(-2, shifts[1]);
        doc.Redo();
        Assert.Equal(new UITextPosition(2, 14), doc.Caret);
        Assert.Equal(2, shifts[2]);
    }

    [Fact]
    public void EditsAfterCompletionUseOriginalCoordinatesAndDoNotMoveCaret()
    {
        var doc = new UITextDocument { Text = "ans + old" };
        Assert.True(doc.ApplyCompletion(new(new(0, 0), new(0, 3), "answer"),
            [new(new(0, 6), new(0, 9), "new\nline")]));
        Assert.Equal("answer + new\nline", doc.Text);
        Assert.Equal(new UITextPosition(0, 6), doc.Caret);
    }

    [Theory]
    [InlineData(0, 1, 0, 2)]
    [InlineData(2, 0, 2, 0)]
    [InlineData(0, 5, 0, 4)]
    [InlineData(-1, 0, 0, 0)]
    public void InvalidOrOverlappingEditRejectsEntireCompletion(int line, int column, int endLine, int endColumn)
    {
        var doc = new UITextDocument { Text = "ans + old" };
        int version = doc.Version;
        Assert.False(doc.ApplyCompletion(new(new(0, 0), new(0, 3), "answer"),
            [new(new(line, column), new(endLine, endColumn), "import")]));
        Assert.Equal("ans + old", doc.Text);
        Assert.False(doc.CanUndo);
        Assert.Equal(version, doc.Version);
    }

    [Fact]
    public void ImportAtReplacementStartPrecedesSymbolAndMovesCaret()
    {
        var doc = new UITextDocument { Text = "ans()\n" };
        doc.MoveTo(new(0, 3));
        Assert.True(doc.ApplyCompletion(new(new(0, 0), new(0, 3), "answer"),
            [new(new(0, 0), new(0, 0), "from helper import answer\n\n")]));
        Assert.Equal("from helper import answer\n\nanswer()\n", doc.Text);
        Assert.Equal(new UITextPosition(2, 6), doc.Caret);
        doc.Undo();
        Assert.Equal("ans()\n", doc.Text);
        Assert.Equal(new UITextPosition(0, 3), doc.Caret);
        Assert.False(doc.CanUndo);
        doc.Redo();
        Assert.Equal("from helper import answer\n\nanswer()\n", doc.Text);
        Assert.Equal(new UITextPosition(2, 6), doc.Caret);
    }

    [Fact]
    public void AmbiguousInsertionsAtSamePositionAreRejected()
    {
        var doc = new UITextDocument { Text = "" };
        Assert.False(doc.ApplyCompletion(new(new(0, 0), new(0, 0), "answer"),
            [new(new(0, 0), new(0, 0), "import")]));
        Assert.Equal("", doc.Text);
        Assert.False(doc.CanUndo);
    }
}
