using Horizon.UI.Components;

namespace Horizon.Tests;

public class UITextDocumentTests
{
    private static UITextDocument Doc(string text, int line = 0, int column = 0)
    {
        var doc = new UITextDocument { Text = text };
        doc.MoveTo(new UITextPosition(line, column));
        return doc;
    }

    private static void Type(UITextDocument doc, string text)
    {
        foreach (char character in text)
            doc.Type(character);
    }

    [Fact]
    public void Typing_goes_in_at_the_caret_and_over_a_selection()
    {
        var doc = Doc("let x = 1;", 0, 4);
        Type(doc, "yy");
        Assert.Equal("let yyx = 1;", doc.Text);

        doc.Select(new UITextPosition(0, 4), new UITextPosition(0, 7));
        Type(doc, "z");
        Assert.Equal("let z = 1;", doc.Text);
        Assert.Equal(new UITextPosition(0, 5), doc.Caret);
    }

    [Fact]
    public void Enter_keeps_the_indentation_and_goes_in_after_a_bracket()
    {
        var doc = Doc("    let a = {}", 0, 13);
        doc.NewLine();
        Assert.Equal("    let a = {\n        \n    }", doc.Text);
        Assert.Equal(new UITextPosition(1, 8), doc.Caret);

        doc = Doc("    let b = 2;", 0, 14);
        doc.NewLine();
        Assert.Equal("    let b = 2;\n    ", doc.Text);
    }

    [Fact]
    public void Brackets_and_quotes_close_themselves_and_are_stepped_over()
    {
        var doc = Doc("");
        Type(doc, "f(");
        Assert.Equal("f()", doc.Text);
        Type(doc, "1)");
        Assert.Equal("f(1)", doc.Text);
        Assert.Equal(4, doc.Caret.Column);

        doc = Doc("");
        Type(doc, "\"hi\"");
        Assert.Equal("\"hi\"", doc.Text);

        // Backspace in an empty pair takes both
        doc = Doc("");
        Type(doc, "[");
        doc.Backspace();
        Assert.Equal("", doc.Text);
    }

    [Fact]
    public void A_closing_bracket_on_a_line_of_its_own_goes_back_out()
    {
        var doc = Doc("{\n        ", 1, 8);
        doc.ClosePairs = false;
        doc.Type('}');
        Assert.Equal("{\n    }", doc.Text);
    }

    [Fact]
    public void Tab_indents_the_lines_of_a_selection_and_shift_tab_takes_them_out()
    {
        var doc = Doc("a\nb\nc");
        doc.Select(new UITextPosition(0, 0), new UITextPosition(1, 1));
        doc.IndentLines();
        Assert.Equal("    a\n    b\nc", doc.Text);

        doc.Outdent();
        Assert.Equal("a\nb\nc", doc.Text);

        // Without a selection over lines it is spaces to the next stop
        doc = Doc("ab", 0, 1);
        doc.IndentLines();
        Assert.Equal("a   b", doc.Text);
    }

    [Fact]
    public void Backspace_in_the_indentation_takes_a_level()
    {
        var doc = Doc("        x", 0, 8);
        doc.Backspace();
        Assert.Equal("    x", doc.Text);
    }

    [Fact]
    public void Comments_go_on_and_come_off_again()
    {
        var doc = Doc("    a();\n    b();");
        doc.Select(new UITextPosition(0, 0), new UITextPosition(1, 3));
        doc.ToggleComment();
        Assert.Equal("    // a();\n    // b();", doc.Text);

        doc.ToggleComment();
        Assert.Equal("    a();\n    b();", doc.Text);
    }

    [Fact]
    public void Up_and_down_keep_to_the_column_they_started_from()
    {
        var doc = Doc("long line here\nab\nanother long one", 0, 10);
        doc.MoveVertically(1, false);
        Assert.Equal(new UITextPosition(1, 2), doc.Caret);
        doc.MoveVertically(1, false);
        Assert.Equal(new UITextPosition(2, 10), doc.Caret);
    }

    [Fact]
    public void Home_goes_to_the_first_thing_on_the_line_and_then_to_the_start()
    {
        var doc = Doc("    let x;", 0, 8);
        doc.MoveHome(false);
        Assert.Equal(4, doc.Caret.Column);
        doc.MoveHome(false);
        Assert.Equal(0, doc.Caret.Column);
    }

    [Fact]
    public void A_run_of_typing_undoes_as_one_step_and_redoes()
    {
        var doc = Doc("x");
        doc.MoveToEnd(false);
        Type(doc, "abc");
        doc.NewLine();
        Type(doc, "de");

        Assert.Equal("xabc\nde", doc.Text);
        doc.Undo();
        Assert.Equal("xabc\n", doc.Text);
        doc.Undo();
        Assert.Equal("xabc", doc.Text);
        doc.Undo();
        Assert.Equal("x", doc.Text);
        Assert.False(doc.CanUndo);

        doc.Redo();
        Assert.Equal("xabc", doc.Text);
    }

    [Fact]
    public void Pasting_lines_puts_the_caret_after_them()
    {
        var doc = Doc("ab", 0, 1);
        doc.Insert("1\r\n2\n3");
        Assert.Equal("a1\n2\n3b", doc.Text);
        Assert.Equal(new UITextPosition(2, 1), doc.Caret);
        Assert.Equal("1\n2", doc.TextBetween(new UITextPosition(0, 1), new UITextPosition(1, 1)));
    }

    [Fact]
    public void Find_goes_round_and_replace_all_is_one_step()
    {
        var doc = Doc("foo bar\nbar foo");
        Assert.True(doc.Find("foo"));
        Assert.Equal(new UITextPosition(0, 0), doc.SelectionStart);
        Assert.True(doc.Find("foo"));
        Assert.Equal(new UITextPosition(1, 4), doc.SelectionStart);
        Assert.True(doc.Find("foo"));
        Assert.Equal(new UITextPosition(0, 0), doc.SelectionStart);
        Assert.False(doc.Find("nope"));

        Assert.Equal(2, doc.ReplaceAll("bar", "baz"));
        Assert.Equal("foo baz\nbaz foo", doc.Text);
        doc.Undo();
        Assert.Equal("foo bar\nbar foo", doc.Text);
    }

    [Fact]
    public void Find_with_options_minds_the_case_whole_words_and_regexes()
    {
        var doc = Doc("Jab jab jabber\nJAB");
        var found = new List<UITextMatch>();

        doc.FindAll("jab", default, found);
        Assert.Equal(4, found.Count);

        doc.FindAll("jab", new UIFindOptions(MatchCase: true), found);
        Assert.Equal([new UITextMatch(0, 4, 3), new UITextMatch(0, 8, 3)], found);

        doc.FindAll("jab", new UIFindOptions(WholeWord: true), found);
        Assert.Equal(3, found.Count);
        Assert.DoesNotContain(new UITextMatch(0, 8, 3), found);

        // A regex's matches are as long as they come out, and a bad one finds nothing and says why
        doc.FindAll(@"jab\w*", new UIFindOptions(Regex: true), found);
        Assert.Contains(new UITextMatch(0, 8, 6), found);
        doc.FindAll("(", new UIFindOptions(Regex: true), found);
        Assert.Empty(found);
        Assert.NotNull(doc.FindError);
    }

    [Fact]
    public void Find_from_a_place_grows_the_match_rather_than_hopping_on()
    {
        var doc = Doc("jump jab jabber");
        var from = new UITextPosition(0, 0);
        Assert.True(doc.FindFrom("j", default, from));
        Assert.Equal(new UITextPosition(0, 0), doc.SelectionStart);
        Assert.True(doc.FindFrom("ja", default, from));
        Assert.Equal(new UITextPosition(0, 5), doc.SelectionStart);
        Assert.True(doc.FindFrom("jabb", default, from));
        Assert.Equal(new UITextPosition(0, 9), doc.SelectionStart);
    }

    [Fact]
    public void Replace_next_swaps_the_selected_match_and_goes_on()
    {
        var doc = Doc("hit: 1, hit: 2");
        var options = new UIFindOptions(Regex: true);

        // Nothing selected that matches, it only finds the first
        Assert.False(doc.ReplaceNext(@"hit: (\d)", "damage: $1", options));
        Assert.Equal("hit: 1", doc.SelectedText);

        Assert.True(doc.ReplaceNext(@"hit: (\d)", "damage: $1", options));
        Assert.Equal("damage: 1, hit: 2", doc.Text);
        Assert.Equal("hit: 2", doc.SelectedText);

        doc.Undo();
        Assert.Equal("hit: 1, hit: 2", doc.Text);
        Assert.Equal(2, doc.ReplaceAll(@"hit: (\d)", "damage: $1", options));
        Assert.Equal("damage: 1, damage: 2", doc.Text);
    }

    [Fact]
    public void Typing_goes_in_at_every_caret_and_one_undo_takes_it_all_back()
    {
        var doc = Doc("jab: 5,\nkick: 7,\nsweep: 9,", 0, 3);
        doc.AddCaret(new UITextPosition(1, 4));
        doc.AddCaret(new UITextPosition(2, 5));
        Assert.Equal(3, doc.CaretCount);

        Type(doc, "_x");
        Assert.Equal("jab_x: 5,\nkick_x: 7,\nsweep_x: 9,", doc.Text);
        Assert.Equal(3, doc.CaretCount);

        doc.Backspace();
        Assert.Equal("jab_: 5,\nkick_: 7,\nsweep_: 9,", doc.Text);

        doc.Undo();
        doc.Undo();
        Assert.Equal("jab: 5,\nkick: 7,\nsweep: 9,", doc.Text);
        Assert.False(doc.HasExtraCarets);
    }

    [Fact]
    public void Carets_on_one_line_each_edit_their_own_place()
    {
        var doc = Doc("a b c", 0, 1);
        doc.AddCaret(new UITextPosition(0, 3));
        doc.AddCaret(new UITextPosition(0, 5));
        Type(doc, "!");
        Assert.Equal("a! b! c!", doc.Text);
        doc.Backspace();
        doc.Backspace();
        Assert.Equal("  ", doc.Text);
    }

    [Fact]
    public void Control_d_takes_the_word_then_every_next_place_of_it()
    {
        var doc = Doc("let speed = speed + speedy;", 0, 6);
        Assert.True(doc.AddNextOccurrence());
        Assert.Equal("speed", doc.SelectedText);
        Assert.True(doc.AddNextOccurrence());
        Assert.True(doc.AddNextOccurrence());
        Assert.Equal(3, doc.CaretCount);

        Type(doc, "pace");
        Assert.Equal("let pace = pace + pacey;", doc.Text);
    }

    [Fact]
    public void Carets_that_run_into_each_other_are_one()
    {
        var doc = Doc("first line", 0, 3);
        doc.AddCaret(new UITextPosition(0, 7));
        doc.MoveHome(false);
        Assert.False(doc.HasExtraCarets);
        Assert.Equal(new UITextPosition(0, 0), doc.Caret);
    }

    [Fact]
    public void A_selection_split_into_line_ends_and_a_column_each_type_on_every_line()
    {
        var doc = Doc("a\nbb\nccc");
        doc.Select(new UITextPosition(0, 0), new UITextPosition(2, 1));
        doc.CaretsAtLineEnds();
        Assert.Equal(3, doc.CaretCount);
        Type(doc, ";");
        Assert.Equal("a;\nbb;\nccc;", doc.Text);

        doc.SelectColumn(new UITextPosition(0, 0), new UITextPosition(2, 1));
        Type(doc, "#");
        Assert.Equal("#;\n#b;\n#cc;", doc.Text);
    }

    [Fact]
    public void Pasting_as_many_lines_as_there_are_carets_hands_out_a_line_each()
    {
        var doc = Doc("x = ;\ny = ;", 0, 4);
        doc.AddCaret(new UITextPosition(1, 4));
        doc.Insert("1\n2\n");
        Assert.Equal("x = 1;\ny = 2;", doc.Text);

        doc.Insert("0");
        Assert.Equal("x = 10;\ny = 20;", doc.Text);
    }

    [Fact]
    public void Lines_move_and_duplicate()
    {
        var doc = Doc("one\ntwo\nthree", 1, 0);
        doc.MoveLines(-1);
        Assert.Equal("two\none\nthree", doc.Text);
        Assert.Equal(0, doc.Caret.Line);

        doc.Duplicate();
        Assert.Equal("two\ntwo\none\nthree", doc.Text);
    }

    [Fact]
    public void Words_are_stepped_over_and_selected()
    {
        var doc = Doc("let text_scale = 0.3;", 0, 0);
        doc.MoveWord(1, false);
        Assert.Equal(4, doc.Caret.Column);
        doc.MoveWord(1, false);
        Assert.Equal(15, doc.Caret.Column);

        doc.SelectWordAt(new UITextPosition(0, 7));
        Assert.Equal("text_scale", doc.SelectedText);
    }

    [Fact]
    public void Dragging_after_a_double_click_takes_whole_words_either_way()
    {
        var doc = Doc("let quick brown fox = jumps;");
        doc.SelectWordAt(new UITextPosition(0, 11));
        var (from, to) = (doc.Anchor, doc.Caret);
        Assert.Equal("brown", doc.SelectedText);

        doc.SelectWordsTo(from, to, new UITextPosition(0, 17));
        Assert.Equal("brown fox", doc.SelectedText);

        doc.SelectWordsTo(from, to, new UITextPosition(0, 5));
        Assert.Equal("quick brown", doc.SelectedText);

        doc.SelectWordsTo(from, to, new UITextPosition(0, 12));
        Assert.Equal("brown", doc.SelectedText);
    }
}
