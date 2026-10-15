using System.Numerics;

using Horizon.UI.Scripting;

namespace Horizon.Tests.UI;

public class HidlHighlighterTests
{
    // What the piece of the line that starts at a column was coloured as
    private static HidlToken KindAt(List<HidlSpan>[] spans, int line, int column) =>
        spans[line].First(span => span.Start <= column && column < span.Start + span.Length).Kind;

    [Fact]
    public void A_name_being_made_is_told_from_one_being_used()
    {
        const string code = "let speed = 3;\nprint(speed);";
        var spans = HidlHighlighter.Read(code, 2);

        Assert.Equal(HidlToken.Declaration, KindAt(spans, 0, 4));
        Assert.Equal(HidlToken.Builtin, KindAt(spans, 1, 0));
        Assert.Equal(HidlToken.Name, KindAt(spans, 1, 6));
    }

    [Fact]
    public void A_functions_parameters_are_declarations_and_its_body_is_not()
    {
        const string code = "func add(a, b) { return a + b; }";
        var spans = HidlHighlighter.Read(code, 1);

        Assert.Equal(HidlToken.Declaration, KindAt(spans, 0, 5));
        Assert.Equal(HidlToken.Declaration, KindAt(spans, 0, 9));
        Assert.Equal(HidlToken.Declaration, KindAt(spans, 0, 12));
        Assert.Equal(HidlToken.Name, KindAt(spans, 0, 24));
        Assert.Equal(HidlToken.Operator, KindAt(spans, 0, 26));
    }

    [Fact]
    public void The_kind_after_compositor_is_a_type_and_constants_are_constants()
    {
        const string code = "let b = compositor.button({ visible: true, tooltip: null });";
        var spans = HidlHighlighter.Read(code, 1);

        Assert.Equal(HidlToken.Builtin, KindAt(spans, 0, 8));
        Assert.Equal(HidlToken.Type, KindAt(spans, 0, 19));
        Assert.Equal(HidlToken.Property, KindAt(spans, 0, 28));
        Assert.Equal(HidlToken.Constant, KindAt(spans, 0, 37));
        Assert.Equal(HidlToken.Constant, KindAt(spans, 0, 52));
    }

    [Fact]
    public void Brackets_go_round_three_colours_by_how_deep_they_are()
    {
        const string code = "f({ a: [(1)] })";
        var spans = HidlHighlighter.Read(code, 1);

        Assert.Equal(HidlToken.Bracket1, KindAt(spans, 0, 1));
        Assert.Equal(HidlToken.Bracket2, KindAt(spans, 0, 2));
        Assert.Equal(HidlToken.Bracket3, KindAt(spans, 0, 7));
        Assert.Equal(HidlToken.Bracket1, KindAt(spans, 0, 8));
        Assert.Equal(HidlToken.Bracket1, KindAt(spans, 0, 10));
        Assert.Equal(HidlToken.Bracket1, KindAt(spans, 0, 14));
    }

    [Fact]
    public void A_text_has_its_key_its_markup_and_its_escapes_picked_out()
    {
        const string code = "let a = \"@main_menu.quit\";\nlet b = \"[b]bold[/b] $(save) line\\n\";";
        var spans = HidlHighlighter.Read(code, 2);

        Assert.Equal(HidlToken.Reference, KindAt(spans, 0, 10));
        Assert.Equal(HidlToken.Markup, KindAt(spans, 1, 9));
        Assert.Equal(HidlToken.Text, KindAt(spans, 1, 12));
        Assert.Equal(HidlToken.Markup, KindAt(spans, 1, 22));
        Assert.Equal(HidlToken.Escape, KindAt(spans, 1, 34));
    }

    [Fact]
    public void A_directive_is_its_word_and_the_name_it_defines()
    {
        var spans = HidlHighlighter.Read("#define layout", 1);

        Assert.Equal(HidlToken.Directive, KindAt(spans, 0, 0));
        Assert.Equal(HidlToken.Constant, KindAt(spans, 0, 8));
    }

    [Fact]
    public void A_colour_written_in_the_code_has_a_swatch_and_another_vec_has_none()
    {
        const string code = "let p = { color: vec(1, 0.5, 0, 0.8), pos: vec(10, 20), text_color: vec(0.2, 0.2, 0.2) };";
        var swatches = new List<HidlSwatch>();
        HidlHighlighter.Read(code, 1, swatches);

        Assert.Equal(2, swatches.Count);
        Assert.Equal(new Vector4(1.0f, 0.5f, 0.0f, 0.8f), swatches[0].Color);
        Assert.Equal(17, swatches[0].Column);
        Assert.Equal("vec(1, 0.5, 0, 0.8)".Length, swatches[0].Length);
        Assert.Equal(1.0f, swatches[1].Color.W);
    }

    [Fact]
    public void Every_line_knows_how_deep_it_starts_and_whether_a_comment_runs_into_it()
    {
        const string code = "let a = {\n    b: [1,\n        2] /* over\n two */\n};";
        var depths = new int[5];
        var clean = new bool[5];
        HidlHighlighter.Read(code, 5, depths: depths, clean: clean);

        Assert.Equal([0, 1, 2, 1, 1], depths);
        Assert.Equal([true, true, true, false, true], clean);
    }

    [Fact]
    public void A_line_on_its_own_comes_out_as_it_does_in_the_whole()
    {
        const string code = "let a = {\n    b: [1, 2]\n};";
        var depths = new int[3];
        var clean = new bool[3];
        var whole = HidlHighlighter.Read(code, 3, depths: depths, clean: clean);

        // The line typed into is coloured alone from the depth it started at, its brackets the colour of their depth
        var alone = HidlHighlighter.ReadLine("    b: [1, 2]", depths[1]);
        Assert.Equal(whole[1], alone);
        Assert.Equal(HidlToken.Bracket2, alone.First(span => span.Start == 7).Kind);
    }
}
