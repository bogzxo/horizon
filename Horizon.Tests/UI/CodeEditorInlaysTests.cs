using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// The hints written in among the code staying put while a line is typed into, the ones either side of the change moved
/// along with the text and only one inside it going, so the code after them doesn't jump left and back.
/// </summary>
public class CodeEditorInlaysTests
{
    private static CodeEditor EditorWith(string text, params UICodeInlay[] hints) => new(text) { InlayHints = hints };

    [Fact]
    public void Typing_before_a_hint_moves_it_along_with_the_text()
    {
        CodeEditor editor = EditorWith("grow(1, 2)\n", new UICodeInlay(0, 5, "size:"), new UICodeInlay(0, 8, "step:"));
        editor.Document.MoveTo(new UITextPosition(0, 0));
        editor.Document.Insert("sdf");

        Assert.Equal([8, 11], editor.InlaysShownOn(0).Select(hint => hint.Column));
    }

    [Fact]
    public void Typing_after_a_hint_leaves_it_where_it_is()
    {
        CodeEditor editor = EditorWith("grow(1, 2)\n", new UICodeInlay(0, 5, "size:"));
        editor.Document.MoveTo(new UITextPosition(0, 10));
        editor.Document.Insert("sdf");

        Assert.Equal([5], editor.InlaysShownOn(0).Select(hint => hint.Column));
    }

    [Fact]
    public void A_hint_inside_what_was_taken_out_goes_and_the_rest_stay()
    {
        CodeEditor editor = EditorWith("grow(1, 2, 3)\n", new UICodeInlay(0, 5, "a:"), new UICodeInlay(0, 8, "b:"), new UICodeInlay(0, 11, "c:"));
        editor.Document.MoveTo(new UITextPosition(0, 6));
        editor.Document.MoveTo(new UITextPosition(0, 10), selecting: true);
        editor.Document.Backspace();

        Assert.Equal([5, 7], editor.InlaysShownOn(0).Select(hint => hint.Column));
    }

    [Fact]
    public void Typed_and_taken_out_again_the_hints_are_as_they_were()
    {
        CodeEditor editor = EditorWith("grow(1, 2)\n", new UICodeInlay(0, 5, "size:"), new UICodeInlay(0, 8, "step:"));
        editor.Document.MoveTo(new UITextPosition(0, 0));
        editor.Document.Insert("sdf");
        editor.Document.MoveTo(new UITextPosition(0, 0), selecting: true);
        editor.Document.Backspace();

        Assert.Equal([5, 8], editor.InlaysShownOn(0).Select(hint => hint.Column));
    }
}
