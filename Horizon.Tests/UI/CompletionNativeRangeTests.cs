using System.Numerics;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public class CompletionNativeRangeTests
{
    [Fact]
    public void NativeRangeReplacesPunctuationAndQuoteWithOneUndo()
    {
        const string source = "#include \"widgets/answer-va\"\nint main() {}\n";
        const string expected = "#include \"widgets/answer-value.h\"\nint main() {}\n";
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = compositor.CreateModule().AddComponent(new CodeEditor(source));
        string line = editor.Document.LineAt(0);
        var caret = new UITextPosition(0, line.Length - 1);
        editor.Document.MoveTo(caret);
        editor.CompletionSource = _ => new(line.LastIndexOf('-') + 1, [new UICompletion("answer-value.h\"",
            ExpectedVersion: editor.Document.Version, ReplacementStart: new(0, line.IndexOf("answer-", StringComparison.Ordinal)), ReplacementEnd: new(0, line.Length))]);
        editor.Complete();
        editor.AcceptCompletion();
        Assert.Equal(expected, editor.Text);
        Assert.Equal(new UITextPosition(0, expected.IndexOf('\n')), editor.Document.Caret);
        editor.Document.Undo();
        Assert.Equal(source, editor.Text);
        Assert.Equal(caret, editor.Document.Caret);
        Assert.False(editor.Document.CanUndo);
        editor.Document.Redo();
        Assert.Equal(expected, editor.Text);
    }

    [Fact]
    public void NativeStartAndImportEditShareOriginalCoordinatesAndUndo()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        const string source = "before some-name\n";
        var editor = compositor.CreateModule().AddComponent(new CodeEditor(source));
        var caret = new UITextPosition(0, source.Length - 1);
        editor.Document.MoveTo(caret);
        editor.CompletionSource = _ => new(12, [new UICompletion("some-name-completed", AdditionalEdits: [new(new(0, 0), new(0, 0), "import\n")],
            ExpectedVersion: editor.Document.Version, ReplacementStart: new(0, 7), ReplacementEnd: caret)]);
        editor.Complete();
        editor.AcceptCompletion();
        Assert.Equal("import\nbefore some-name-completed\n", editor.Text);
        editor.Document.Undo();
        Assert.Equal(source, editor.Text);
        Assert.Equal(caret, editor.Document.Caret);
        Assert.False(editor.Document.CanUndo);
    }

    [Fact]
    public void StaleNativeStartRequestsFreshCompletionAndLeavesTextUntouched()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = compositor.CreateModule().AddComponent(new CodeEditor("some-name"));
        editor.Document.MoveTo(new(0, 9));
        int asked = 0, originalVersion = editor.Document.Version;
        editor.CompletionSource = _ =>
        {
            asked++;
            return new(5, [new UICompletion("some-name-completed", ExpectedVersion: originalVersion, ReplacementStart: new(0, 0))]);
        };
        editor.Complete();
        editor.Document.Type('x');
        editor.AcceptCompletion();
        Assert.Equal("some-namex", editor.Text);
        Assert.Equal(2, asked);
    }
}
