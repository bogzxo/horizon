using System.Numerics;
using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public class CodeEditorCompletionSelectionTests
{
    [Fact]
    public void NativeReplacementAndSelectionPreserveSuffixAndUndoAsOneEdit()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = compositor.CreateModule().AddComponent(new CodeEditor("box.ca + other"));
        editor.Document.MoveTo(new(0, 6));
        int version = editor.Document.Version;
        editor.CompletionSource = _ => new(4, [new("call", Insert: "call(arg)", ExpectedVersion: version,
            ReplacementStart: new(0, 4), ReplacementEnd: new(0, 6), SelectionStart: 5, SelectionLength: 3)]);
        editor.Complete();
        editor.AcceptCompletion();
        Assert.Equal("box.call(arg) + other", editor.Text);
        Assert.Equal("arg", editor.Document.SelectedText);
        editor.Document.Undo();
        Assert.Equal("box.ca + other", editor.Text);
    }

    [Fact]
    public void SelectionFollowsAdditionalImportsAndNormalizedLineEndings()
    {
        var document = new UITextDocument { Text = "ca + other" };
        document.MoveTo(new(0, 2));
        Assert.True(document.ApplyCompletion(new(new(0, 0), new(0, 2), "call(\r\narg)"),
            [new(new(0, 0), new(0, 0), "import\n")], selectionStart: 7, selectionLength: 3));
        Assert.Equal("import\ncall(\narg) + other", document.Text);
        Assert.Equal("arg", document.SelectedText);
        document.Undo();
        Assert.Equal("ca + other", document.Text);
    }

    [Fact]
    public void ACompletionCannotBeAcceptedAfterItsExternalContextRetires()
    {
        bool current = true;
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = compositor.CreateModule().AddComponent(new CodeEditor("va"));
        editor.Document.MoveTo(new(0, 2));
        editor.CompletionSource = _ => new(0, [new("value")]);
        editor.CompletionAvailable = () => current;
        editor.Complete();
        Assert.True(editor.CompletionOpen);
        current = false;
        editor.AcceptCompletion();
        Assert.Equal("va", editor.Text);
        Assert.False(editor.CompletionOpen);
    }
}
