using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>
/// The lenses over lines (Visual Studio's code lens counts) staying over the lines they're about as lines are put in and
/// taken out above them, until the program says them again, and the counts a server's lens titles say.
/// </summary>
public class CodeEditorLensesAboveTests
{
    private static CodeEditor EditorWith(string text, params UICodeLensAbove[] lenses) => new(text) { LensesAbove = lenses };

    [Fact]
    public void Lenses_are_kept_in_order_of_their_place()
    {
        CodeEditor editor = EditorWith("a\nb = 1, c = 2\n", new UICodeLensAbove(1, 8, "c"), new UICodeLensAbove(0, 0, "a"), new UICodeLensAbove(1, 0, "b"));

        Assert.Equal(["a", "b", "c"], editor.LensesAbove.Select(lens => lens.Text));
    }

    [Fact]
    public void A_line_put_in_above_moves_a_lens_down_with_its_line()
    {
        CodeEditor editor = EditorWith("one\ntwo\nthree\n", new UICodeLensAbove(2, 0, "three"), new UICodeLensAbove(0, 0, "one"));
        editor.Document.MoveTo(new UITextPosition(1, 0));
        editor.Document.Insert("zero\n");

        Assert.Equal(3, editor.LensesAbove.Single(lens => lens.Text == "three").Line);
        Assert.Equal(0, editor.LensesAbove.Single(lens => lens.Text == "one").Line);
    }

    [Fact]
    public void Enter_before_a_declarations_text_takes_its_lens_down_with_it_and_after_it_does_not()
    {
        CodeEditor editor = EditorWith("    void Go() { }\n", new UICodeLensAbove(0, 9, "go"));
        editor.Document.MoveTo(new UITextPosition(0, 4));
        editor.Document.Insert("\n");
        Assert.Equal(1, editor.LensesAbove.Single().Line);

        editor = EditorWith("    void Go() { }\n", new UICodeLensAbove(0, 9, "go"));
        editor.Document.MoveTo(new UITextPosition(0, 15));
        editor.Document.Insert("\n");
        Assert.Equal(0, editor.LensesAbove.Single().Line);
    }

    [Fact]
    public void Lines_taken_out_above_move_it_up_and_one_on_them_goes()
    {
        CodeEditor editor = EditorWith("a\nb\nc\nd\ne\n", new UICodeLensAbove(4, 0, "below"), new UICodeLensAbove(2, 0, "inside"));
        editor.Document.MoveTo(new UITextPosition(1, 1));
        editor.Document.MoveTo(new UITextPosition(3, 0), selecting: true);
        editor.Document.Backspace();

        Assert.Equal(2, editor.LensesAbove.Single(lens => lens.Text == "below").Line);
        Assert.DoesNotContain(editor.LensesAbove, lens => lens.Text == "inside");
    }

    [Fact]
    public void A_line_joined_onto_one_with_a_lens_of_its_own_leaves_that_one_alone()
    {
        CodeEditor editor = EditorWith("int a;\nint b;\n", new UICodeLensAbove(0, 4, "a"), new UICodeLensAbove(1, 4, "b"));
        editor.Document.MoveTo(new UITextPosition(1, 0));
        editor.Document.Backspace();

        Assert.Equal("a", editor.LensesAbove.Single().Text);
        Assert.Equal(0, editor.LensesAbove.Single().Line);
    }

    [Fact]
    public void Nothing_is_in_sight_before_the_first_paint()
    {
        Assert.Equal(-1, EditorWith("a\nb\n").LinesInSight.Last);
    }

    [Theory]
    [InlineData("2 Reference(s)", 2)]
    [InlineData("1 reference", 1)]
    [InlineData("0 references", 0)]
    [InlineData("12 usages", 12)]
    [InlineData("Run Test", null)]
    [InlineData("3 implementations", null)]
    public void A_lens_title_says_how_many_references(string title, int? count)
    {
        Assert.Equal(count, LanguageServer.ReferenceCountOf(title));
    }

    [Fact]
    public void A_row_going_while_its_line_is_typed_on_stays_until_said_again_for_the_same_text()
    {
        CodeEditor editor = EditorWith("let a = 1;\nlet speed = 3;\n", new UICodeLensAbove(0, 4, "a"), new UICodeLensAbove(1, 4, "speed"));
        editor.Document.MoveTo(new UITextPosition(1, 0));
        editor.Document.Insert("sdf");

        // sdflet speed declares nothing for a moment, its row stays rather than the lines under it jumping up
        editor.LensesAbove = [new UICodeLensAbove(0, 4, "a")];
        Assert.Contains(editor.LensesAbove, lens => lens.Line == 1 && lens.Text == "speed");

        // Said again for the text as it is, it's as said
        editor.LensesAbove = [new UICodeLensAbove(0, 4, "a")];
        Assert.DoesNotContain(editor.LensesAbove, lens => lens.Line == 1);
    }

    [Fact]
    public void A_row_coming_on_the_line_being_typed_waits()
    {
        CodeEditor editor = EditorWith("let a = 1;\n\n", new UICodeLensAbove(0, 4, "a"));
        editor.Document.MoveTo(new UITextPosition(1, 0));
        editor.Document.Insert("let b = 2;");

        editor.LensesAbove = [new UICodeLensAbove(0, 4, "a"), new UICodeLensAbove(1, 4, "b")];
        Assert.DoesNotContain(editor.LensesAbove, lens => lens.Line == 1);
    }

    [Fact]
    public void Everything_let_go_of_goes_at_once_typed_into_or_not()
    {
        CodeEditor editor = EditorWith("let a = 1;\n", new UICodeLensAbove(0, 4, "a"));
        editor.Document.MoveTo(new UITextPosition(0, 0));
        editor.Document.Insert("x");

        editor.LensesAbove = [];
        Assert.Empty(editor.LensesAbove);
    }
}
