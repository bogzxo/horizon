using System.Numerics;
using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public class CodeEditorTypedEventTests
{
    private sealed class TypingEditor : CodeEditor { public void Type(char character) => OnTextInput(character); }

    [Fact]
    public void TypedEventSeesPairedTextAndNeverTreatsUndoOrProgrammaticChangesAsTyping()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = compositor.CreateModule().AddComponent(new TypingEditor());
        editor.CompletionSource = _ => null;
        var typed = new List<(char Character, string Text, UITextPosition Caret)>();
        editor.OnTyped = character => typed.Add((character, editor.Text, editor.Document.Caret));
        editor.Type('(');
        Assert.Equal(('(', "()", new UITextPosition(0, 1)), Assert.Single(typed));
        editor.Document.Insert("value");
        editor.Type(UIKeyboard.UNDO);
        Assert.Single(typed);
        editor.ReadOnly = true;
        editor.Type(';');
        Assert.Single(typed);
    }
}
