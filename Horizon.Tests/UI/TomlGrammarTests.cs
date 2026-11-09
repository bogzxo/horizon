using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>
/// TOML, coloured by the engine's own grammar (Scripting/Grammars/toml.tmLanguage.json), TextMateSharp having none.
/// Its comments, tables, keys plain and quoted and dotted, its strings over several lines, dates and numbers.
/// </summary>
public class TomlGrammarTests
{
    private static SyntaxKind KindAt(List<SyntaxSpan>[] spans, int line, int column) =>
        spans[line].FirstOrDefault(span => span.Start <= column && column < span.Start + span.Length, new SyntaxSpan(0, 0, SyntaxKind.Name)).Kind;

    [Fact]
    public void Toml_files_are_known_by_their_extension_and_their_id()
    {
        var highlighter = Assert.IsType<TextMateHighlighter>(CodeLanguages.ForFile("Cargo.toml"));
        Assert.Equal("source.toml", highlighter.ScopeName);
        Assert.Equal("TOML", highlighter.Name);
        Assert.Same(highlighter, CodeLanguages.ForLanguageId("toml"));
        Assert.Equal("toml", CodeLanguages.LanguageIdOf(highlighter));
        Assert.Contains(CodeLanguages.Languages(), language => language.Id == "toml");
    }

    [Fact]
    public void Toml_is_coloured()
    {
        string code = "# a comment\n"
            + "[package]\n"
            + "name = \"demo\" # after\n"
            + "\"quoted key\".part = 1979-05-27\n"
            + "text = \"\"\"\n"
            + "still the text\n"
            + "\"\"\"\n"
            + "list = [1, 2.5e3, true]\n";
        var spans = CodeLanguages.ForFile("demo.toml")!.Read(code, code.Count(c => c == '\n') + 1);

        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 0, 4));
        Assert.Equal(SyntaxKind.Type, KindAt(spans, 1, 3));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 2, 0));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 2, 8));
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 2, 16));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 3, 3));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 3, 15));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 3, 23));

        // The string over three lines is a string all the way down
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 5, 2));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 6, 0));

        Assert.Equal(SyntaxKind.Number, KindAt(spans, 7, 8));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 7, 11));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 7, 18));
    }
}
