using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// What's wrong in a code editor staying on the code it's about as lines are put in and taken out above it, until
/// whoever said it says it again, a language server a moment after the typing stops.
/// </summary>
public class CodeEditorDiagnosticsTests
{
    private static CodeEditor EditorWith(string text, params UICodeDiagnostic[] found)
    {
        var editor = new CodeEditor(text);
        editor.Diagnostics = found;
        return editor;
    }

    [Fact]
    public void A_line_put_in_above_moves_it_down_with_its_code()
    {
        CodeEditor editor = EditorWith("one\ntwo\nthree\n", new UICodeDiagnostic(2, 0, 5, "wrong"));
        editor.Document.MoveTo(new UITextPosition(0, 0));
        editor.Document.Insert("zero\n");

        Assert.Equal(3, editor.Diagnostics.Single().Line);
    }

    [Fact]
    public void Put_in_on_its_own_line_before_it_moves_it_after_it_does_not()
    {
        CodeEditor editor = EditorWith("int x = ;\n", new UICodeDiagnostic(0, 4, 1, "before"), new UICodeDiagnostic(0, 0, 3, "at the start"));
        editor.Document.MoveTo(new UITextPosition(0, 3));
        editor.Document.Insert("\n");

        Assert.Equal(1, editor.Diagnostics.Single(found => found.Message == "before").Line);
        Assert.Equal(0, editor.Diagnostics.Single(found => found.Message == "at the start").Line);
    }

    [Fact]
    public void Lines_taken_out_above_move_it_up_and_one_on_them_goes_where_they_were()
    {
        CodeEditor editor = EditorWith("a\nb\nc\nd\ne\n", new UICodeDiagnostic(4, 0, 1, "below"), new UICodeDiagnostic(2, 0, 1, "inside"));
        editor.Document.MoveTo(new UITextPosition(1, 0));
        editor.Document.MoveTo(new UITextPosition(3, 0), selecting: true);
        editor.Document.Backspace();

        Assert.Equal(2, editor.Diagnostics.Single(found => found.Message == "below").Line);
        Assert.Equal(1, editor.Diagnostics.Single(found => found.Message == "inside").Line);
    }

    [Fact]
    public void A_hint_is_a_warning_as_well_and_says_it_is_only_a_hint()
    {
        var hint = new UICodeDiagnostic(0, 0, 1, "never used", Warning: true, Hint: true);
        Assert.True(hint.Warning);
        Assert.True(hint.Hint);
        Assert.False(new UICodeDiagnostic(0, 0, 1, "wrong").Hint);
    }
}
