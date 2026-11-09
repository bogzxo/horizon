using Horizon.UI.Components;

namespace Horizon.Tests;

public class NativeTextEditsTests
{
    [Fact]
    public void ImportAndRenamePreserveSelectionAndReplayLineMovementsTogether()
    {
        var doc = new UITextDocument { Text = "first\nold();\nlast" };
        doc.Select(new(1, 0), new(1, 3));
        List<int> movements = [];
        int changes = 0;
        doc.LinesShifted += (_, by, _) => movements.Add(by);
        doc.Changed += () => changes++;
        Assert.True(doc.ApplyEdits([new(new(1, 0), new(1, 3), "renamed"), new(new(0, 0), new(0, 0), "using Tools;\n\n")]));
        Assert.Equal("using Tools;\n\nfirst\nrenamed();\nlast", doc.Text);
        Assert.Equal("renamed", doc.SelectedText);
        Assert.Equal(new UITextPosition(3, 7), doc.Caret);
        Assert.Equal(new UITextPosition(3, 0), doc.Anchor);
        Assert.Equal(1, changes);
        Assert.Equal(2, Assert.Single(movements));
        doc.Undo();
        Assert.Equal("first\nold();\nlast", doc.Text);
        Assert.Equal("old", doc.SelectedText);
        Assert.False(doc.CanUndo);
        Assert.Equal(-2, movements[1]);
        doc.Redo();
        Assert.Equal("renamed", doc.SelectedText);
        Assert.Equal(2, movements[2]);
    }

    [Fact]
    public void SameLineEditsMoveCaretByCharacterDelta()
    {
        var doc = new UITextDocument { Text = "short + value" };
        doc.MoveTo(new(0, 10));
        Assert.True(doc.ApplyEdits([new(new(0, 0), new(0, 5), "muchLonger")]));
        Assert.Equal(new UITextPosition(0, 15), doc.Caret);
        Assert.Equal(doc.Caret, doc.Anchor);
        doc.Undo();
        Assert.Equal(new UITextPosition(0, 10), doc.Caret);
    }

    [Fact]
    public void InvalidBatchDoesNotPartiallyApplyOrConsumeHistory()
    {
        var doc = new UITextDocument { Text = "original" };
        doc.Insert("prior ");
        UITextEdit[] edits = [new(new(0, 0), new(0, 0), "valid\n"), new(new(2, 0), new(2, 0), "invalid")];
        Assert.False(doc.CanApplyEdits(edits));
        Assert.False(doc.ApplyEdits(edits));
        Assert.Equal("prior original", doc.Text);
        doc.Undo();
        Assert.Equal("original", doc.Text);
        Assert.False(doc.CanUndo);
    }
}
