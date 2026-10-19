using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>
/// Every other language, coloured by VS Code's grammars through TextMateSharp and the scopes mapped onto the skin's
/// kinds. One file of each of the languages Hex is expected to open, and the mapping on its own.
/// </summary>
public class TextMateHighlighterTests
{
    private static SyntaxKind KindAt(List<SyntaxSpan>[] spans, int line, int column) =>
        spans[line].FirstOrDefault(span => span.Start <= column && column < span.Start + span.Length, new SyntaxSpan(0, 0, SyntaxKind.Name)).Kind;

    private static List<SyntaxSpan>[] Read(string extension, string code)
    {
        ICodeHighlighter? highlighter = CodeLanguages.ForExtension(extension);
        Assert.NotNull(highlighter);
        return highlighter.Read(code, code.Count(c => c == '\n') + 1);
    }

    [Fact]
    public void A_file_nothing_has_a_grammar_for_is_plain_text()
    {
        Assert.Null(CodeLanguages.ForExtension(".nothing-at-all"));
        Assert.Null(CodeLanguages.ForFile("notes.txt"));
        Assert.Null(CodeLanguages.ForExtension(""));
    }

    [Fact]
    public void The_languages_hex_opens_are_known_by_their_extension()
    {
        Assert.Equal("HIDL", CodeLanguages.ForFile("layout.hor")!.Name);
        Assert.Same(CodeLanguages.Hidl, CodeLanguages.ForFile("moves.jab"));
        Assert.Same(CodeLanguages.Hidl, CodeLanguages.ForFile("game.hex-workspace"));
        Assert.Equal("source.cs", ((TextMateHighlighter)CodeLanguages.ForFile("Thing.cs")!).ScopeName);
        Assert.Equal("source.python", ((TextMateHighlighter)CodeLanguages.ForFile("make.py")!).ScopeName);
        Assert.Equal("source.json", ((TextMateHighlighter)CodeLanguages.ForFile("data.json")!).ScopeName);
        Assert.Equal("source.hlsl", ((TextMateHighlighter)CodeLanguages.ForFile("sprites.slang")!).ScopeName);
        Assert.Equal("text.html.markdown", ((TextMateHighlighter)CodeLanguages.ForFile("README.md")!).ScopeName);

        // The same one every time, a grammar is loaded once
        Assert.Same(CodeLanguages.ForFile("A.cs"), CodeLanguages.ForFile("B.CS"));
    }

    [Fact]
    public void C_sharp_is_coloured()
    {
        var spans = Read(".cs", "using System;\n\nnamespace Demo;\n\n// a comment\npublic class Thing\n{\n    public int Count { get; set; } = 3;\n    private string name = \"hello\";\n}\n");

        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Type, KindAt(spans, 2, 10));
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 4, 3));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 5, 0));
        Assert.Equal(SyntaxKind.Type, KindAt(spans, 5, 13));
        Assert.Equal(SyntaxKind.Bracket1, KindAt(spans, 6, 0));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 7, 11));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 7, 15));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 7, 37));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 8, 27));
    }

    [Fact]
    public void Python_is_coloured()
    {
        var spans = Read(".py", "import os\n\ndef greet(name):\n    # say hello\n    print(len(name), 42)\n    return f\"hi {name}\"\n");

        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 2, 0));
        Assert.Equal(SyntaxKind.Call, KindAt(spans, 2, 4));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 2, 10));
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 3, 4));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 4, 4));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 4, 10));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 4, 21));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 5, 4));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 5, 13));
    }

    [Fact]
    public void Json_is_coloured()
    {
        var spans = Read(".json", "{\n    \"name\": \"hex\",\n    \"count\": 3,\n    \"on\": true\n}\n");

        Assert.Equal(SyntaxKind.Bracket1, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 1, 5));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 1, 13));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 2, 13));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 3, 10));
    }

    [Fact]
    public void A_slang_shader_is_coloured_as_hlsl()
    {
        var spans = Read(".slang", "#include <common/camera.slang>\n\nstruct Vertex { float4 position : SV_Position; };\n\n// the vertex stage\nVertex vsMain(uint id : SV_VertexID) { return (Vertex)0; }\n");

        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 2, 0));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 2, 16));
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 4, 0));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 5, 39));
    }

    [Fact]
    public void Markdown_is_coloured()
    {
        var spans = Read(".md", "# Title\n\nSome *words* and `code` with a [link](https://x.y).\n\n- item\n");

        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 3));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 2, 6));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 2, 18));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 4, 0));
    }

    [Fact]
    public void A_block_comment_carries_over_the_lines_and_a_line_inside_it_is_read_alone_from_its_start()
    {
        const string code = "int a; /* over\nthe lines\n*/ int b;";
        var starts = new CodeLineStart[3];
        var spans = CodeLanguages.ForExtension(".cs")!.Read(code, 3, starts);

        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 1, 0));
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 2, 0));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 2, 3));
        Assert.Null(starts[0].State);
        Assert.NotNull(starts[1].State);

        var alone = CodeLanguages.ForExtension(".cs")!.ReadLine("the lines", starts[1]);
        Assert.Equal(SyntaxKind.Comment, alone.Single().Kind);
    }

    [Fact]
    public void Scopes_map_onto_kinds_by_their_deepest_prefix()
    {
        Assert.Equal(SyntaxKind.Comment, SyntaxScopes.KindOf(["source.cs", "comment.block.cs", "storage.type.class.jsdoc"]));
        Assert.Equal(SyntaxKind.Escape, SyntaxScopes.KindOf(["source.cs", "string.quoted.double.cs", "constant.character.escape.cs"]));
        Assert.Equal(SyntaxKind.Text, SyntaxScopes.KindOf(["source.cs", "string.quoted.double.cs", "punctuation.definition.string.begin.cs"]));
        Assert.Equal(SyntaxKind.Call, SyntaxScopes.KindOf(["source.python", "meta.function-call.python", "meta.function-call.generic.python"]));
        Assert.Equal(SyntaxKind.Name, SyntaxScopes.KindOf(["source.python", "meta.function-call.python", "meta.function-call.arguments.python"]));
        Assert.Equal(SyntaxKind.Directive, SyntaxScopes.KindOf(["source.c", "meta.preprocessor.include.c", "keyword.control.directive.include.c"]));
        Assert.Equal(SyntaxKind.Constant, SyntaxScopes.KindOf(["source.c", "meta.preprocessor.macro.c", "entity.name.function.preprocessor.c"]));
        Assert.Equal(SyntaxKind.Property, SyntaxScopes.KindOf(["source.json", "meta.structure.dictionary.json", "string.json", "support.type.property-name.json"]));
        Assert.Equal(SyntaxKind.Reference, SyntaxScopes.KindOf(["text.html.markdown", "meta.link.inline.markdown", "string.other.link.title.markdown"]));
        Assert.Equal(SyntaxKind.Name, SyntaxScopes.KindOf(["source.cs"]));
        Assert.Equal(SyntaxKind.Name, SyntaxScopes.KindOf(["source.cs", "meta.something.nobody.maps"]));
    }

    [Fact]
    public void Brackets_count_outside_of_texts_and_comments_only()
    {
        Assert.True(SyntaxScopes.HoldsBrackets(["source.cs", "meta.block.cs"]));
        Assert.False(SyntaxScopes.HoldsBrackets(["source.cs", "string.quoted.double.cs"]));
        Assert.False(SyntaxScopes.HoldsBrackets(["source.cs", "comment.line.double-slash.cs"]));
        Assert.False(SyntaxScopes.HoldsBrackets(["text.html.markdown", "markup.underline.link.markdown"]));
    }
}
