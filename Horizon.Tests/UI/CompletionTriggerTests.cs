using System.Numerics;

using Horizon.Engine;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

[Collection("UI focus restoration")]
public class CompletionTriggerTests
{
    private sealed class TypingEditor : CodeEditor
    {
        public void Type(char character) => OnTextInput(character);
    }

    [Theory]
    [InlineData(':')]
    [InlineData('<')]
    [InlineData('"')]
    [InlineData('[')]
    public void AdvertisedTriggerOpensAndKeepsAnEmptyPrefixCompletion(char trigger)
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = compositor.CreateModule().AddComponent(new TypingEditor());
        int requests = 0;
        editor.CompletionTrigger = character => character == trigger;
        editor.CompletionSource = current =>
        {
            requests++;
            return new(current.Document.Caret.Column, [new UICompletion("answer")]);
        };
        editor.Type(trigger);
        Assert.Equal(1, requests);
        Assert.True(editor.CompletionTyped);
        Assert.True(editor.CompletionOpen);
        Assert.Equal("answer", Assert.Single(editor.Completions).Text);
        string closing = trigger == '[' ? "]" : trigger == '"' ? "\"" : "";
        Assert.Equal(trigger + closing, editor.Text);
        editor.AcceptCompletion();
        Assert.Equal(trigger + "answer" + closing, editor.Text);
    }

    [Fact]
    public void UnadvertisedPunctuationClosesCompletionAndDoesNotAskAgain()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = compositor.CreateModule().AddComponent(new TypingEditor());
        int requests = 0;
        editor.CompletionTrigger = character => character == ':';
        editor.CompletionSource = current =>
        {
            requests++;
            return new(0, [new UICompletion("answer")]);
        };
        editor.Type('a');
        Assert.True(editor.CompletionOpen);
        editor.Type(';');
        Assert.False(editor.CompletionOpen);
        Assert.Equal(1, requests);
        Assert.Equal("a;", editor.Text);
    }

    [Fact]
    public void WaitingForNativeTriggerSurvivesUntilItsAnswerArrives()
    {
        using var compositor = new UICompositor(new Camera2D(new Vector2(800, 600)));
        var editor = compositor.CreateModule().AddComponent(new TypingEditor());
        editor.CompletionTrigger = character => character == ':';
        bool answered = false;
        editor.CompletionSource = current => answered ? new(1, [new UICompletion("answer")]) : new(1, [], "Waiting");
        editor.Type(':');
        Assert.True(editor.CompletionOpen);
        Assert.Equal("Waiting", editor.CompletionWaiting);
        answered = true;
        editor.Complete(typed: true);
        Assert.True(editor.CompletionOpen);
        Assert.Equal("answer", Assert.Single(editor.Completions).Text);
    }
}
