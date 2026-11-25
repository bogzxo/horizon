using Horizon.Input;
using Horizon.Platform;
using Horizon.UI.Components;

namespace Horizon.Tests.Platform;

/// <summary>Tests for native dialog filter normalization and inline IME preedit composition in TextBox and CodeEditor.</summary>
public class MacDialogsAndImeTests
{
    [Fact]
    public void ExtensionsOf_normalizes_multi_pattern_filters()
    {
        string[] exts = PlatformDialogFilters.ExtensionsOf([
            ("Horizon Layouts", "*.hor;*.JSON"),
            ("Text", ".txt, md")
        ]);

        Assert.Equal(["hor", "json", "txt", "md"], exts);
    }

    [Fact]
    public void ExtensionsOf_deduplicates_extensions_preserving_order()
    {
        string[] exts = PlatformDialogFilters.ExtensionsOf([
            ("First", "*.hor;*.json"),
            ("Second", "*.HOR;*.yaml")
        ]);

        Assert.Equal(["hor", "json", "yaml"], exts);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.*")]
    [InlineData("*.hor;*.*")]
    public void ExtensionsOf_returns_empty_for_wildcard_filters(string pattern)
    {
        string[] exts = PlatformDialogFilters.ExtensionsOf([("All", pattern)]);
        Assert.Empty(exts);
    }

    [Fact]
    public void ExtensionsOf_handles_null_empty_and_custom_extensions()
    {
        Assert.Empty(PlatformDialogFilters.ExtensionsOf(null));
        Assert.Empty(PlatformDialogFilters.ExtensionsOf([]));

        string[] workspace = PlatformDialogFilters.ExtensionsOf([("Hex Workspaces", "*.hex-workspace")]);
        Assert.Equal(["hex-workspace"], workspace);
    }

    [Fact]
    public void InputEvent_Composition_populates_fields()
    {
        var ev = InputEvent.Composition("にほん", 2, 1);

        Assert.Equal(InputEventKind.Composition, ev.Kind);
        Assert.Equal("にほん", ev.Text);
        Assert.Equal(2, ev.CompositionCursor);
        Assert.Equal(1, ev.CompositionLength);
    }

    [Fact]
    public void TextBox_composition_keeps_text_unchanged_until_commit_and_handles_emoji()
    {
        var box = new TextBox("hi ");

        // Preedit does not mutate Text
        box.OnComposition("´", 1, 0);
        Assert.Equal("hi ", box.Text);
        Assert.Equal("´", box.CompositionText);
        Assert.Equal(1, box.CompositionCursor);

        // Commit via typed characters (including surrogate-pair emoji "🚀") clears preedit and inserts text
        foreach (char c in "🚀")
            box.OnTextInput(c);

        Assert.Equal(string.Empty, box.CompositionText);
        Assert.Equal(0, box.CompositionCursor);
        Assert.Equal("hi 🚀", box.Text);
    }

    [Fact]
    public void TextBox_clears_composition_on_focus_lost()
    {
        var box = new TextBox("abc");
        box.OnComposition("にほ", 2, 0);
        Assert.Equal("にほ", box.CompositionText);

        box.OnFocusLost();

        Assert.Equal(string.Empty, box.CompositionText);
        Assert.Equal(0, box.CompositionCursor);
        Assert.Equal("abc", box.Text);
    }

    [Fact]
    public void CodeEditor_composition_keeps_document_unchanged_until_commit_and_handles_emoji()
    {
        var editor = new CodeEditor("let x = ");
        editor.Document.MoveTo(new UITextPosition(0, editor.Text.Length));

        // Preedit does not mutate Document
        editor.OnComposition("にほん", 3, 0);
        Assert.Equal("let x = ", editor.Text);
        Assert.Equal("にほん", editor.CompositionText);
        Assert.Equal(3, editor.CompositionCursor);

        // Commit via typed characters (surrogate-pair emoji "🚀") clears preedit and commits into Document
        foreach (char c in "🚀")
            editor.OnTextInput(c);

        Assert.Equal(string.Empty, editor.CompositionText);
        Assert.Equal(0, editor.CompositionCursor);
        Assert.Equal("let x = 🚀", editor.Text);
    }

    [Fact]
    public void CodeEditor_clears_composition_on_focus_lost()
    {
        var editor = new CodeEditor("fn main() {}");
        editor.OnComposition("´", 1, 0);
        Assert.Equal("´", editor.CompositionText);

        editor.OnFocusLost();

        Assert.Equal(string.Empty, editor.CompositionText);
        Assert.Equal(0, editor.CompositionCursor);
        Assert.Equal("fn main() {}", editor.Text);
    }
}
