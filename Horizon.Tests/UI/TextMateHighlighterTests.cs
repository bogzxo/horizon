using Horizon.UI.Components;
using Horizon.UI.Scripting;
using Horizon.UI.Skinning;

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

    [Theory]
    [InlineData(".cs", "int value = 42;")]
    [InlineData(".py", "value = 42")]
    [InlineData(".cpp", "int value = 42;")]
    [InlineData(".hor", "let value = 42;")]
    public async Task PaintedLineDoesNotWaitForBackgroundGrammarAndRecoversWhenAvailable(string extension, string line)
    {
        var highlighter = CodeLanguages.ForExtension(extension)!;
        var textMate = highlighter is HidlHighlighter hidl ? hidl.Grammar : Assert.IsType<TextMateHighlighter>(highlighter);
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object grammar = typeof(TextMateHighlighter).GetField("grammar", hidden)!.GetValue(textMate)!;
        var editor = new CodeEditor(line) { Highlighter = highlighter };
        typeof(CodeEditor).GetMethod("Recolour", hidden)!.Invoke(editor, null);
        editor.Document.MoveTo(new UITextPosition(0, line.Length));
        editor.Document.Type(' ');
        string changed = line + " ";
        var freshColours = typeof(CodeEditor).GetMethod("FreshColours", hidden)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holding = Task.Run(() => { lock (grammar) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } });
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            // On a thread of its own, the pool's were all taken by the rest of the tests at once and a second went by
            // before the paint even began. Five seconds is still half of the ten the grammar is held for, a paint
            // that waited for it would take those
            object? deferred = await Task.Factory.StartNew(() => freshColours.Invoke(editor, [0, changed]), TaskCreationOptions.LongRunning).WaitAsync(TimeSpan.FromSeconds(5));
            if (highlighter is HidlHighlighter)
            {
                // HidlHighlighter uses a lock-free native scanner and never contends with the TextMate grammar lock.
                var immediate = Assert.IsType<List<SyntaxSpan>>(deferred);
                Assert.Equal(highlighter.ReadLine(changed, CodeLineStart.First), immediate);
            }
            else
            {
                Assert.Null(deferred);
            }
        }
        finally { release.Set(); await holding; }
        // Other tests and the background recolourer share this grammar. A later paint can still defer while one
        // of them owns it; the editor retries on its next frame rather than waiting for the grammar lock.
        object? recovered = null;
        for (int attempt = 0; attempt < 100 && recovered is null; attempt++)
        {
            recovered = freshColours.Invoke(editor, [0, changed]);
            if (recovered is null) await Task.Delay(10);
        }
        var spans = Assert.IsType<List<SyntaxSpan>>(recovered);
        Assert.Equal(highlighter.ReadLine(changed, CodeLineStart.First), spans);
        Assert.Same(spans, freshColours.Invoke(editor, [0, changed]));
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

    // An agent's chat saved as JSON, one line of a fifth of a megabyte, strings with commas and escaped quotes in them
    private static readonly string LongJson = "[" + string.Join(",", Enumerable.Range(0, 4000).Select(i =>
        $"{{\"kind\":\"Tool\",\"seconds\":{i}.5,\"text\":\"a, b and \\\"c\\\", {i}\",\"stopped\":false}}")) + "]";

    // What every letter of a line is coloured as
    private static SyntaxKind[] Kinds(List<SyntaxSpan> spans, int length)
    {
        var kinds = new SyntaxKind[length];
        Array.Fill(kinds, SyntaxKind.Name);
        foreach (SyntaxSpan span in spans)
            kinds.AsSpan(span.Start, span.Length).Fill(span.Kind);
        return kinds;
    }

    [Fact]
    public void A_line_too_long_to_read_at_once_is_read_in_pieces_to_its_end_and_the_next_line_goes_on_from_it()
    {
        var spans = Read(".json", LongJson + "\n{\"next\": 2}\n");

        LongLineSpans read = Assert.IsType<LongLineSpans>(spans[0]);
        Assert.True(read.Whole);
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 0, LongJson.LastIndexOf("3999.5", StringComparison.Ordinal)));
        Assert.Equal(KindAt(spans, 0, LongJson.IndexOf("false", StringComparison.Ordinal)), KindAt(spans, 0, LongJson.LastIndexOf("false", StringComparison.Ordinal)));
        Assert.Equal(KindAt(spans, 0, LongJson.IndexOf("a, b", StringComparison.Ordinal)), KindAt(spans, 0, LongJson.LastIndexOf("a, b", StringComparison.Ordinal)));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 1, 9));
    }

    [Fact]
    public void A_long_line_read_as_far_as_what_is_in_sight_is_read_on_from_there_and_comes_out_as_read_whole()
    {
        var highlighter = (TextMateHighlighter)CodeLanguages.ForExtension(".json")!;
        var whole = Assert.IsType<LongLineSpans>(highlighter.ReadLine(LongJson, CodeLineStart.First));

        // As far as a place, no further than a piece or two past it, then on to the end from there
        var some = Assert.IsType<LongLineSpans>(highlighter.ReadUpTo(LongJson, CodeLineStart.First, 30_000, int.MaxValue, null, null));
        Assert.InRange(some.ReadTo, 30_000, 30_000 + TextMateHighlighter.PIECE * 2);
        Assert.DoesNotContain(some, span => span.Start >= some.ReadTo);

        var budgeted = Assert.IsType<LongLineSpans>(highlighter.ReadUpTo(LongJson, CodeLineStart.First, LongJson.Length, 10_000, some, LongJson));
        Assert.InRange(budgeted.ReadTo, some.ReadTo + 10_000, some.ReadTo + 10_000 + TextMateHighlighter.PIECE * 2);

        var rest = Assert.IsType<LongLineSpans>(highlighter.ReadUpTo(LongJson, CodeLineStart.First, LongJson.Length, int.MaxValue, budgeted, LongJson));
        Assert.True(rest.Whole);
        Assert.Equal(Kinds(whole, LongJson.Length), Kinds(rest, LongJson.Length));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("\"")]
    [InlineData("[{")]
    [InlineData("")]
    public void A_long_line_typed_into_is_read_again_from_the_edit_and_comes_out_as_read_whole(string typed)
    {
        var highlighter = (TextMateHighlighter)CodeLanguages.ForExtension(".json")!;
        var before = Assert.IsType<LongLineSpans>(highlighter.ReadLine(LongJson, CodeLineStart.First));

        // Typed into the middle, or a letter taken out there
        int at = LongJson.Length / 2;
        string edited = typed.Length > 0 ? LongJson.Insert(at, typed) : LongJson.Remove(at, 1);
        var again = Assert.IsType<LongLineSpans>(highlighter.ReadUpTo(edited, CodeLineStart.First, edited.Length, int.MaxValue, before, LongJson));
        var whole = Assert.IsType<LongLineSpans>(highlighter.ReadLine(edited, CodeLineStart.First));

        Assert.True(again.Whole);
        Assert.Equal(Kinds(whole, edited.Length), Kinds(again, edited.Length));
    }

    [Fact]
    public void A_letter_typed_into_a_long_line_reads_a_piece_or_two_again_not_all_of_it()
    {
        var highlighter = (TextMateHighlighter)CodeLanguages.ForExtension(".json")!;
        var before = Assert.IsType<LongLineSpans>(highlighter.ReadLine(LongJson, CodeLineStart.First));

        // With no more letters to read than a few pieces it still gets to the end, the rest is the last read moved along
        string edited = LongJson.Insert(LongJson.Length / 2, "x");
        var again = Assert.IsType<LongLineSpans>(highlighter.ReadUpTo(edited, CodeLineStart.First, edited.Length, TextMateHighlighter.PIECE * 4, before, LongJson));
        Assert.True(again.Whole);
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

        Assert.Equal(SyntaxKind.Control, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 2, 0));
        Assert.Equal(SyntaxKind.Call, KindAt(spans, 2, 4));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 2, 10));
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 3, 4));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 4, 4));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 4, 10));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 4, 21));
        Assert.Equal(SyntaxKind.Control, KindAt(spans, 5, 4));
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
        Assert.Equal(SyntaxKind.Control, KindAt(spans, 5, 39));
    }

    [Fact]
    public void C_sharp_control_keywords_are_apart_from_the_other_keywords()
    {
        var spans = Read(".cs", "if (ready)\n    return 1;\nint x = 2;\n");

        Assert.Equal(SyntaxKind.Control, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Control, KindAt(spans, 1, 4));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 2, 0));
    }

    [Fact]
    public void A_language_given_colours_of_its_own_is_painted_in_them_and_the_rest_in_the_skins()
    {
        var palette = new UIPalette();
        UISyntaxColors dark = UISyntaxColors.VisualStudioDark(), light = UISyntaxColors.VisualStudioLight();
        CodeLanguages.SetColors("source.cs", dark, light);

        Assert.Same(palette.LightField ? light : dark, CodeLanguages.ColorsFor(CodeLanguages.ForExtension(".cs"), palette));
        Assert.Same(palette.Syntax, CodeLanguages.ColorsFor(CodeLanguages.ForExtension(".py"), palette));
        Assert.Same(palette.Syntax, CodeLanguages.ColorsFor(null, palette));

        // Visual Studio's keywords are blue and its control keywords purple, and a skin that never set control has them as keywords
        Assert.NotEqual(dark.Keyword, dark.Control);
        Assert.Equal(palette.Syntax.Keyword, palette.Syntax.Control);
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
    public void Xml_comments_are_coloured_between_their_marks()
    {
        // TextMateSharp's grammar names the marks and not what's between them, CodeLanguages mends it
        var spans = Read(".xml", "<root>\n  <!-- one line -->\n  <!--\n    over lines\n  -->\n</root>\n");

        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 1, 7));
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 3, 6));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 5, 3));
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
