using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>
/// D, coloured by the engine's own grammar (Scripting/Grammars/d.tmLanguage.json), there being none in VS Code or
/// TextMateSharp. Its comments that nest, its strings of every kind, its attributes and what a declaration names.
/// </summary>
public class DGrammarTests
{
    private static SyntaxKind KindAt(List<SyntaxSpan>[] spans, int line, int column) =>
        spans[line].FirstOrDefault(span => span.Start <= column && column < span.Start + span.Length, new SyntaxSpan(0, 0, SyntaxKind.Name)).Kind;

    [Fact]
    public void D_files_are_known_by_both_their_extensions()
    {
        var highlighter = Assert.IsType<TextMateHighlighter>(CodeLanguages.ForFile("app.d"));
        Assert.Equal("source.d", highlighter.ScopeName);
        Assert.Equal("D", highlighter.Name);
        Assert.Same(highlighter, CodeLanguages.ForFile("std/stdio.di"));
    }

    [Fact]
    public void D_is_coloured()
    {
        string code = "module demo.app;\n"
            + "import std.stdio;\n"
            + "/+ nested /+ comment +/ still +/\n"
            + "@safe struct Point { int x = 0x1F; }\n"
            + "void main() {\n"
            + "    writeln(r\"raw\\n\", 'c');\n"
            + "    static if (true) {}\n"
            + "}\n";
        var spans = CodeLanguages.ForFile("app.d")!.Read(code, code.Count(c => c == '\n') + 1);

        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Type, KindAt(spans, 0, 7));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 1, 0));

        // Still a comment after the one inside it is over, D's comments of that kind nest
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 2, 25));

        Assert.Equal(SyntaxKind.Property, KindAt(spans, 3, 0));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 3, 6));
        Assert.Equal(SyntaxKind.Type, KindAt(spans, 3, 13));
        Assert.Equal(SyntaxKind.Type, KindAt(spans, 3, 21));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 3, 29));

        Assert.Equal(SyntaxKind.Type, KindAt(spans, 4, 0));
        Assert.Equal(SyntaxKind.Call, KindAt(spans, 4, 5));

        Assert.Equal(SyntaxKind.Call, KindAt(spans, 5, 4));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 5, 15));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 5, 23));

        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 6, 4));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 6, 15));
    }
}
