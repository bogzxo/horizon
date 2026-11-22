using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// The card the pointer gets over something wrong in a code editor: how bad it is in its colour with the code apart from
/// what it says, every diagnostic at the place, what's said of the word under a rule after them, made once while the
/// pointer rests on one place.
/// </summary>
public class CodeEditorDiagnosticCardTests
{
    [Theory]
    [InlineData("The name 'totel' does not exist (CS0103)", "The name 'totel' does not exist", "CS0103")]
    [InlineData("CS0168: The variable 'spare' is never used", "The variable 'spare' is never used", "CS0168")]
    [InlineData("Import \"numpy\" could not be resolved (reportMissingImports)", "Import \"numpy\" could not be resolved", "reportMissingImports")]
    [InlineData("undefined identifier x (dmd)", "undefined identifier x", "dmd")]
    [InlineData("an argument is missing (optional)", "an argument is missing (optional)", "")]
    [InlineData("expected ; (got a b)", "expected ; (got a b)", "")]
    public void The_code_is_taken_off_the_message(string message, string said, string code) =>
        Assert.Equal((said, code), CodeEditor.SplitCode(message));

    [Fact]
    public void The_card_says_how_bad_each_is_its_code_and_then_the_word()
    {
        var editor = new CodeEditor("int total = totel;\n")
        {
            Diagnostics =
            [
                new UICodeDiagnostic(0, 12, 5, "The name 'totel' does not exist (CS0103)") { DocumentationUrl = "https://example.com/CS0103" },
                new UICodeDiagnostic(0, 12, 5, "Rename it (IDE1006)", Warning: true, Hint: true),
            ],
        };

        string card = editor.DiagnosticCard(new UITextPosition(0, 13), "```cs\nint totel\n```");

        Assert.StartsWith("[color:error][b]Error[/b][/color]  [CS0103](https://example.com/CS0103)\nThe name 'totel' does not exist", card);
        Assert.Contains("\n\n[color:dim][b]Hint[/b][/color]  [color:dim]IDE1006[/color]\nRename it", card);
        Assert.EndsWith("\n\n---\n\n```cs\nint totel\n```", card);
    }

    [Fact]
    public void Resting_on_the_same_place_makes_it_once()
    {
        var editor = new CodeEditor("x = y;\n") { Diagnostics = [new UICodeDiagnostic(0, 4, 1, "y? (E1)", Warning: true)] };
        string said = "about y";

        string first = editor.DiagnosticCard(new UITextPosition(0, 4), said);
        Assert.Same(first, editor.DiagnosticCard(new UITextPosition(0, 4), said));
        Assert.StartsWith("[color:warning][b]Warning[/b][/color]", first);
    }
}
