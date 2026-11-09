using System.Numerics;

using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

/// <summary>
/// HIDL coloured through its own TextMate grammar with the lexer's knowledge laid over it. What these expect is what
/// the lexer alone coloured before the grammar came, nothing a .hor showed in Hex is allowed to go.
/// </summary>
public class HidlHighlighterTests
{
    private static readonly HidlHighlighter Hidl = HidlHighlighter.Instance;

    // What the piece of the line that starts at a column was coloured as
    private static SyntaxKind KindAt(List<SyntaxSpan>[] spans, int line, int column) =>
        spans[line].First(span => span.Start <= column && column < span.Start + span.Length).Kind;

    [Fact]
    public void A_name_being_made_is_told_from_one_being_used()
    {
        const string code = "let speed = 3;\nprint(speed);";
        var spans = Hidl.Read(code, 2);

        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 4));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 0, 12));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 1, 0));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 1, 6));
    }

    [Fact]
    public void A_functions_parameters_are_declarations_and_its_body_is_not()
    {
        const string code = "func add(a, b) { return a + b; }";
        var spans = Hidl.Read(code, 1);

        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 5));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 9));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 12));
        Assert.Equal(SyntaxKind.Control, KindAt(spans, 0, 17));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 0, 24));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 0, 26));
    }

    [Fact]
    public void An_arrows_parameters_and_the_names_a_let_takes_apart_are_declarations()
    {
        const string code = "let f = (a, b = 2) => a + b;\nlet g = x => x;\nlet { left, right } = sides;";
        var spans = Hidl.Read(code, 3);

        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 9));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 12));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 0, 16));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 0, 19));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 0, 22));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 1, 8));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 1, 10));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 1, 13));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 2, 6));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 2, 12));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 2, 22));
    }

    [Fact]
    public void A_default_is_a_use_and_the_names_after_it_are_still_parameters()
    {
        const string code = "func box(at, size = vec(at, 2), colour) { }\nfor (key, value in things) { }\nfor (let item in list) { }";
        var spans = Hidl.Read(code, 3);

        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 9));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 13));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 0, 24));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 0, 32));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 1, 5));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 1, 10));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 1, 19));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 2, 9));

        // The grammar on its own, what VS Code sees, has the names of a for as declared too
        var grammar = Hidl.Grammar.Read(code, 3, null);
        Assert.Equal(SyntaxKind.Control, KindAt(grammar, 1, 0));
        Assert.Equal(SyntaxKind.Declaration, KindAt(grammar, 1, 5));
        Assert.Equal(SyntaxKind.Declaration, KindAt(grammar, 1, 10));
        Assert.Equal(SyntaxKind.Control, KindAt(grammar, 1, 16));
        Assert.Equal(SyntaxKind.Declaration, KindAt(grammar, 2, 9));
    }

    [Fact]
    public void The_kind_after_compositor_is_a_type_and_constants_are_constants()
    {
        const string code = "let b = compositor.button({ visible: true, tooltip: null });";
        var spans = Hidl.Read(code, 1);

        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 0, 8));
        Assert.Equal(SyntaxKind.Type, KindAt(spans, 0, 19));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 0, 28));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 0, 37));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 0, 52));
    }

    [Fact]
    public void Brackets_go_round_three_colours_by_how_deep_they_are()
    {
        const string code = "f({ a: [(1)] })";
        var spans = Hidl.Read(code, 1);

        Assert.Equal(SyntaxKind.Call, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Bracket1, KindAt(spans, 0, 1));
        Assert.Equal(SyntaxKind.Bracket2, KindAt(spans, 0, 2));
        Assert.Equal(SyntaxKind.Bracket3, KindAt(spans, 0, 7));
        Assert.Equal(SyntaxKind.Bracket1, KindAt(spans, 0, 8));
        Assert.Equal(SyntaxKind.Bracket1, KindAt(spans, 0, 10));
        Assert.Equal(SyntaxKind.Bracket1, KindAt(spans, 0, 14));
    }

    [Fact]
    public void A_text_has_its_key_its_markup_and_its_escapes_picked_out()
    {
        const string code = "let a = \"@main_menu.quit\";\nlet b = \"[b]bold[/b] $(save) line\\n\";";
        var spans = Hidl.Read(code, 2);

        Assert.Equal(SyntaxKind.Reference, KindAt(spans, 0, 8));
        Assert.Equal(SyntaxKind.Reference, KindAt(spans, 0, 10));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 1, 8));
        Assert.Equal(SyntaxKind.Markup, KindAt(spans, 1, 9));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 1, 12));
        Assert.Equal(SyntaxKind.Markup, KindAt(spans, 1, 22));
        Assert.Equal(SyntaxKind.Escape, KindAt(spans, 1, 34));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 1, 35));
        Assert.Equal(SyntaxKind.Punctuation, KindAt(spans, 1, 36));
    }

    [Fact]
    public void Brackets_inside_a_text_are_markup_and_not_brackets()
    {
        const string code = "let a = \"[icon:pad_a] (hi) [b]\";";
        var spans = Hidl.Read(code, 1);

        Assert.All(spans[0].Where(span => span.Start > 8 && span.Start < 30), span => Assert.True(span.Kind is SyntaxKind.Markup or SyntaxKind.Text, span.ToString()));
    }

    [Fact]
    public void A_directive_is_its_word_and_the_name_it_defines()
    {
        var spans = Hidl.Read("#define layout\n#if defined(layout) && !debug\n#endif", 3);

        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 0, 0));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 0, 8));
        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 1, 0));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 1, 12));
        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 2, 1));
    }

    [Fact]
    public void A_template_is_a_text_with_code_inside_of_it()
    {
        const string code = "let t = `{count} left, {names.first ?? \"nobody\"}`;";
        var spans = Hidl.Read(code, 1);

        Assert.Equal(SyntaxKind.Text, KindAt(spans, 0, 8));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 0, 10));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 0, 18));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 0, 30));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 0, 36));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 0, 40));
    }

    [Fact]
    public void The_newer_operators_are_operators()
    {
        const string code = "let a = b ?? c?.d; a += 1; let e = { ...a };";
        var spans = Hidl.Read(code, 1);

        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 0, 10));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 0, 14));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 0, 16));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 0, 21));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 0, 37));
        Assert.Equal(SyntaxKind.Punctuation, KindAt(spans, 0, 17));
    }

    [Fact]
    public void A_colour_written_in_the_code_has_a_swatch_and_another_vec_has_none()
    {
        const string code = "let p = { color: vec(1, 0.5, 0, 0.8), pos: vec(10, 20), text_color: vec(0.2, 0.2, 0.2) };";
        var swatches = new List<HidlSwatch>();
        Hidl.Read(code, 1, swatches: swatches);

        Assert.Equal(2, swatches.Count);
        Assert.Equal(new Vector4(1.0f, 0.5f, 0.0f, 0.8f), swatches[0].Color);
        Assert.Equal(17, swatches[0].Column);
        Assert.Equal("vec(1, 0.5, 0, 0.8)".Length, swatches[0].Length);
        Assert.Equal(1.0f, swatches[1].Color.W);
    }

    [Fact]
    public void Art_named_in_the_code_is_found_on_its_line_and_other_texts_are_not()
    {
        const string code = "let a = { text: \"$(play) go [icon:pad_a]\", icon: \"save\",\n"
            + "    sprite: \"button\", file: \"art/hero.aseprite\", logo: \"logo.PNG\", title: \"hello [b]there[/b]\" };";
        var art = new List<HidlArt>();
        Hidl.Read(code, 2, art: art);

        Assert.Equal(
            [
                new HidlArt(0, 17, 7, HidlArtKind.Symbol, "play"),
                new HidlArt(0, 28, 12, HidlArtKind.Icon, "pad_a"),
                new HidlArt(0, 50, 4, HidlArtKind.Symbol, "save"),
                new HidlArt(1, 13, 6, HidlArtKind.Sprite, "button"),
                new HidlArt(1, 29, 17, HidlArtKind.Picture, "art/hero.aseprite"),
                new HidlArt(1, 56, 8, HidlArtKind.Picture, "logo.PNG")
            ],
            art);
    }

    [Fact]
    public void An_icon_after_a_set_of_icons_is_one_of_that_set()
    {
        var art = new List<HidlArt>();
        Hidl.Read("let a = \"[icon:pad_a] or [icons:playstation][icon:pad_a]\";", 1, art: art);

        Assert.Equal(2, art.Count);
        Assert.Null(art[0].Set);
        Assert.Equal(("pad_a", "playstation"), (art[1].Name, art[1].Set));
    }

    [Fact]
    public void Every_line_knows_how_deep_it_starts_and_a_comment_that_runs_into_it()
    {
        const string code = "let a = {\n    b: [1,\n        2] /* over\n two */\n};";
        var starts = new CodeLineStart[5];
        var whole = Hidl.Read(code, 5, starts);

        Assert.Equal([0, 1, 2, 1, 1], starts.Select(start => start.Depth));
        Assert.Equal(SyntaxKind.Comment, KindAt(whole, 3, 1));

        // The line inside the comment coloured on its own from where it starts is still a comment, the grammar's state says so
        var alone = Hidl.ReadLine(" two */", starts[3]);
        Assert.Equal(SyntaxKind.Comment, alone.Single().Kind);
    }

    [Fact]
    public void A_line_on_its_own_comes_out_as_it_does_in_the_whole()
    {
        const string code = "let a = {\n    b: [1, 2]\n};";
        var starts = new CodeLineStart[3];
        var whole = Hidl.Read(code, 3, starts);

        // The line typed into is coloured alone from the depth it started at, its brackets the colour of their depth
        var alone = Hidl.ReadLine("    b: [1, 2]", starts[1]);
        Assert.Equal(whole[1], alone);
        Assert.Equal(SyntaxKind.Bracket2, alone.First(span => span.Start == 7).Kind);
    }

    [Fact]
    public void The_grammar_alone_colours_a_file_with_a_bit_of_everything_the_way_the_language_says()
    {
        // What VS Code sees, with nothing of the lexer's over it. The declarations, the parameters, the kind of a
        // component and the language's own words are the grammar's as well, so a .hor reads the same there
        const string code = """
            // A layout, more or less
            #define layout
            #if defined(layout) && !debug
            let count = 3;
            const names = ["a", "b"];
            func greet(who, times) { return `hi {who}` + "\n"; }
            let panel = compositor.panel({ color: vec(0.2, 0.4, 0.9, 1), text: "[b]bold[/b] $(save)", title: "@main_menu.quit" });
            /* over
               two lines */
            print(names.first ?? panel?.title, len(names), true, null);
            #endif
            """;
        int lines = code.Count(c => c == '\n') + 1;
        var spans = Hidl.Grammar.Read(code, lines);

        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 0, 3));
        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 1, 0));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 1, 8));
        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 2, 1));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 2, 4));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 2, 12));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 2, 20));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 3, 0));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 3, 4));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 3, 12));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 4, 0));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 4, 6));
        Assert.Equal(SyntaxKind.Bracket1, KindAt(spans, 4, 14));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 4, 15));
        Assert.Equal(SyntaxKind.Keyword, KindAt(spans, 5, 0));
        Assert.Equal(SyntaxKind.Call, KindAt(spans, 5, 5));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 5, 11));
        Assert.Equal(SyntaxKind.Declaration, KindAt(spans, 5, 16));
        Assert.Equal(SyntaxKind.Control, KindAt(spans, 5, 25));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 5, 32));
        Assert.Equal(SyntaxKind.Name, KindAt(spans, 5, 37));
        Assert.Equal(SyntaxKind.Escape, KindAt(spans, 5, 47));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 6, 12));
        Assert.Equal(SyntaxKind.Type, KindAt(spans, 6, 23));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 6, 31));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 6, 38));
        Assert.Equal(SyntaxKind.Number, KindAt(spans, 6, 42));
        Assert.Equal(SyntaxKind.Markup, KindAt(spans, 6, 68));
        Assert.Equal(SyntaxKind.Text, KindAt(spans, 6, 71));
        Assert.Equal(SyntaxKind.Markup, KindAt(spans, 6, 80));
        Assert.Equal(SyntaxKind.Reference, KindAt(spans, 6, 98));
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 7, 0));
        Assert.Equal(SyntaxKind.Comment, KindAt(spans, 8, 3));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 9, 0));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 9, 12));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 9, 18));
        Assert.Equal(SyntaxKind.Operator, KindAt(spans, 9, 26));
        Assert.Equal(SyntaxKind.Property, KindAt(spans, 9, 28));
        Assert.Equal(SyntaxKind.Builtin, KindAt(spans, 9, 35));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 9, 47));
        Assert.Equal(SyntaxKind.Constant, KindAt(spans, 9, 53));
        Assert.Equal(SyntaxKind.Directive, KindAt(spans, 10, 1));
    }

    [Fact]
    public void The_grammar_is_the_one_in_the_language_and_vs_code_reads_the_same_file()
    {
        string grammar = Horizon.HIDL.HIDLGrammar.Read();
        Assert.Contains("\"scopeName\": \"source.hidl\"", grammar);

        // The extension next to it says which file it colours with, the one that is built in
        string? repo = FindUp("Horizon.HIDL");
        Assert.NotNull(repo);
        string package = File.ReadAllText(Path.Combine(repo, "editors", "vscode", "package.json"));
        Assert.Contains("\"path\": \"./syntaxes/hidl.tmLanguage.json\"", package);
        Assert.Equal(grammar, File.ReadAllText(Path.Combine(repo, "editors", "vscode", "syntaxes", "hidl.tmLanguage.json")));
    }

    private static string? FindUp(string folder)
    {
        for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
        {
            string here = Path.Combine(at, folder);
            if (Directory.Exists(here))
                return here;
        }

        return null;
    }
}
