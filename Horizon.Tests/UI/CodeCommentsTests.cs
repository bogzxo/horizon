using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

using Key = Silk.NET.Input.Key;

namespace Horizon.Tests.UI;

/// <summary>
/// Comments in the language's own way (CodeComments), Ctrl+/ and Shift+Alt+A on a document with them, and VS Code's
/// keys for lines, copied up and down, Ctrl+L and Ctrl+Shift+L, as the keyboard turns them into codes.
/// </summary>
public class CodeCommentsTests
{
    private static UITextDocument Doc(string text, CodeComment comment, int line = 0, int column = 0)
    {
        var doc = new UITextDocument { Text = text, LineComment = comment.Line, BlockComment = (comment.BlockStart, comment.BlockEnd) };
        doc.MoveTo(new UITextPosition(line, column));
        return doc;
    }

    [Theory]
    [InlineData("source.cs", "//", "/*", "*/")]
    [InlineData("source.python", "#", "", "")]
    [InlineData("source.shell", "#", "", "")]
    [InlineData("source.yaml", "#", "", "")]
    [InlineData("source.powershell", "#", "<#", "#>")]
    [InlineData("source.sql", "--", "/*", "*/")]
    [InlineData("source.lua", "--", "--[[", "]]")]
    [InlineData("text.html.basic", "", "<!--", "-->")]
    [InlineData("text.html.markdown", "", "<!--", "-->")]
    [InlineData("text.xml", "", "<!--", "-->")]
    [InlineData("source.css", "", "/*", "*/")]
    [InlineData("source.css.scss", "//", "/*", "*/")]
    [InlineData("source.json.comments", "//", "/*", "*/")]
    [InlineData("source.ini", ";", "", "")]
    [InlineData("text.tex.latex", "%", "", "")]
    [InlineData("source.hidl", "//", "/*", "*/")]
    [InlineData("python", "#", "", "")]
    public void A_language_comments_its_own_way(string scope, string line, string blockStart, string blockEnd)
    {
        Assert.Equal(new CodeComment(line, blockStart, blockEnd), CodeComments.For(scope));
    }

    [Fact]
    public void The_highlighters_give_their_languages_comments()
    {
        Assert.Equal("#", CodeComments.For(CodeLanguages.ForFile("make.py")).Line);
        Assert.Equal("--", CodeComments.For(CodeLanguages.ForFile("query.sql")).Line);
        Assert.Equal("<!--", CodeComments.For(CodeLanguages.ForFile("page.html")).BlockStart);
        Assert.Equal("//", CodeComments.For(CodeLanguages.ForFile("layout.hor")).Line);
        Assert.Equal(CodeComment.None, CodeComments.For((ICodeHighlighter?)null));
    }

    [Fact]
    public void A_language_not_in_the_table_is_asked_of_vs_codes_configuration()
    {
        // Julia is left out of the table on purpose, its language-configuration.json says #
        CodeComment julia = CodeComments.For("source.julia");
        Assert.Equal("#", julia.Line);
        Assert.Equal(CodeComment.None, CodeComments.For("source.nothing-has-this"));
    }

    [Fact]
    public void Ctrl_slash_writes_the_languages_line_comment()
    {
        var python = Doc("    x = 1\n    y = 2", CodeComments.For("source.python"), 0, 6);
        python.ToggleComment();
        Assert.Equal("    # x = 1\n    y = 2", python.Text);
        Assert.Equal(new UITextPosition(0, 8), python.Caret);
        python.ToggleComment();
        Assert.Equal("    x = 1\n    y = 2", python.Text);

        var sql = Doc("select 1;", CodeComments.For("source.sql"));
        sql.ToggleComment();
        Assert.Equal("-- select 1;", sql.Text);
    }

    [Fact]
    public void Ctrl_slash_wraps_the_lines_in_a_block_when_there_is_no_line_comment()
    {
        var html = Doc("  <p>hi</p>\n  <b>yo</b>", CodeComments.For("text.html.basic"), 0, 4);
        html.ToggleComment();
        Assert.Equal("  <!-- <p>hi</p> -->\n  <b>yo</b>", html.Text);
        Assert.Equal(new UITextPosition(0, 9), html.Caret);
        html.ToggleComment();
        Assert.Equal("  <p>hi</p>\n  <b>yo</b>", html.Text);

        html.SelectAll();
        html.ToggleComment();
        Assert.Equal("  <!-- <p>hi</p>\n  <b>yo</b> -->", html.Text);
        html.ToggleComment();
        Assert.Equal("  <p>hi</p>\n  <b>yo</b>", html.Text);

        var css = Doc("a { color: red; }", CodeComments.For("source.css"));
        css.ToggleComment();
        Assert.Equal("/* a { color: red; } */", css.Text);
    }

    [Fact]
    public void Shift_alt_a_puts_the_selection_in_a_block_comment_and_back()
    {
        var doc = Doc("int x = 1;", CodeComments.For("source.cs"));
        doc.Select(new UITextPosition(0, 4), new UITextPosition(0, 5));
        doc.ToggleBlockComment();
        Assert.Equal("int /* x */ = 1;", doc.Text);
        Assert.Equal("x", doc.SelectedText);

        // Still selected inside of it, again takes it back out
        doc.ToggleBlockComment();
        Assert.Equal("int x = 1;", doc.Text);
        Assert.Equal("x", doc.SelectedText);

        // Nothing selected an empty one goes in with the caret in its middle, and again inside of it takes it off
        var empty = Doc("f();", CodeComments.For("source.cs"), 0, 2);
        empty.ToggleBlockComment();
        Assert.Equal("f(/*  */);", empty.Text);
        Assert.Equal(new UITextPosition(0, 5), empty.Caret);
        empty.ToggleBlockComment();
        Assert.Equal("f();", empty.Text);

        // Python has no block comment, it gets its line comment
        var python = Doc("x = 1", CodeComments.For("source.python"));
        python.ToggleBlockComment();
        Assert.Equal("# x = 1", python.Text);
    }

    [Fact]
    public void Lines_copy_down_and_up_with_the_caret_going_along_down()
    {
        var doc = Doc("a\nb\nc", CodeComment.None, 1, 1);
        doc.CopyLines(1);
        Assert.Equal("a\nb\nb\nc", doc.Text);
        Assert.Equal(new UITextPosition(2, 1), doc.Caret);

        doc.CopyLines(-1);
        Assert.Equal("a\nb\nb\nb\nc", doc.Text);
        Assert.Equal(new UITextPosition(2, 1), doc.Caret);

        // A selection over two lines copies both, one undo takes it back
        var two = Doc("a\nb\nc", CodeComment.None);
        two.Select(new UITextPosition(0, 0), new UITextPosition(1, 1));
        two.CopyLines(1);
        Assert.Equal("a\nb\na\nb\nc", two.Text);
        Assert.Equal(new UITextPosition(3, 1), two.Caret);
        two.Undo();
        Assert.Equal("a\nb\nc", two.Text);
    }

    [Fact]
    public void Lines_copy_at_every_caret()
    {
        var doc = Doc("a\nb\nc", CodeComment.None, 0, 0);
        doc.AddCaretVertically(1);
        doc.AddCaretVertically(1);
        doc.CopyLines(1);
        Assert.Equal("a\na\nb\nb\nc\nc", doc.Text);
        Assert.Equal(3, doc.CaretCount);
    }

    [Fact]
    public void Ctrl_l_selects_the_line_and_then_the_next()
    {
        var doc = Doc("one\ntwo\nthree", CodeComment.None, 0, 1);
        doc.ExpandLineSelection();
        Assert.Equal("one\n", doc.SelectedText);
        doc.ExpandLineSelection();
        Assert.Equal("one\ntwo\n", doc.SelectedText);
        doc.ExpandLineSelection();
        Assert.Equal("one\ntwo\nthree", doc.SelectedText);
    }

    [Fact]
    public void Ctrl_shift_l_selects_every_place_the_word_is_as_a_word()
    {
        var doc = Doc("hit hitter hit\nhit", CodeComment.None, 0, 12);
        Assert.True(doc.SelectAllOccurrences());
        Assert.Equal(3, doc.CaretCount);
        Assert.Equal(new UITextPosition(0, 11), doc.Anchor);
        Assert.Equal("hit\nhit\nhit", doc.SelectedTextOfEvery());

        // A selection is found inside of words too
        var part = Doc("hit hitter", CodeComment.None);
        part.Select(new UITextPosition(0, 0), new UITextPosition(0, 3));
        Assert.True(part.SelectAllOccurrences());
        Assert.Equal(2, part.CaretCount);
    }

    [Fact]
    public void The_keys_are_vs_codes()
    {
        Assert.Equal(UIKeyboard.COPY_LINES_DOWN, UIKeyboard.Meaning(Key.Down, primary: false, shift: true, alt: true, mac: false).Code);
        Assert.Equal(UIKeyboard.COPY_LINES_UP, UIKeyboard.Meaning(Key.Up, primary: false, shift: true, alt: true, mac: false).Code);
        Assert.Equal(UIKeyboard.MOVE_LINES_DOWN, UIKeyboard.Meaning(Key.Down, primary: false, shift: false, alt: true, mac: false).Code);
        Assert.Equal(UIKeyboard.CARET_BELOW, UIKeyboard.Meaning(Key.Down, primary: true, shift: false, alt: true, mac: false).Code);
        Assert.Equal(UIKeyboard.CARET_ABOVE, UIKeyboard.Meaning(Key.Up, primary: true, shift: false, alt: true, mac: false).Code);
        Assert.Equal(UIKeyboard.SELECT_LINE, UIKeyboard.Meaning(Key.L, primary: true, shift: false, alt: false, mac: false).Code);
        Assert.Equal(UIKeyboard.SELECT_ALL_OCCURRENCES, UIKeyboard.Meaning(Key.L, primary: true, shift: true, alt: false, mac: false).Code);
        Assert.Equal(UIKeyboard.CARETS_AT_LINE_ENDS, UIKeyboard.Meaning(Key.I, primary: false, shift: true, alt: true, mac: false).Code);
        Assert.Equal(UIKeyboard.TOGGLE_BLOCK_COMMENT, UIKeyboard.Meaning(Key.A, primary: false, shift: true, alt: true, mac: false).Code);
        Assert.Equal(UIKeyboard.DUPLICATE, UIKeyboard.Meaning(Key.D, primary: true, shift: true, alt: false, mac: false).Code);
        Assert.Equal(UIKeyboard.SELECT_ALL, UIKeyboard.Meaning(Key.A, primary: true, shift: false, alt: false, mac: false).Code);
    }
}
