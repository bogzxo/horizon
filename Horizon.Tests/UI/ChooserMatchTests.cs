using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>
/// Which letters of a chooser's row light up for what was typed, the same ways the rows are ranked by, and a row in its
/// parts known by the text it would have been written as before rows had parts.
/// </summary>
public sealed class ChooserMatchTests
{
    [Fact]
    public void What_a_row_starts_with_lights_up_as_one_piece()
    {
        // "sa" is how Save All starts, the S of its first word and the a after it, the way Rank puts it first
        Assert.Equal([(0, 2)], UIChooser.MatchSpans("Save All", "sa"));
        Assert.Equal(0, UIChooser.Rank("Save All", "sa"));
    }

    [Fact]
    public void Letters_each_starting_a_word_light_up_the_s_and_the_a()
    {
        Assert.Equal([(0, 1), (5, 1)], UIChooser.MatchSpans("Save All", "s a"));
        Assert.Equal([(0, 1), (5, 1)], UIChooser.MatchSpans("Show All", "sa"));
    }

    [Fact]
    public void A_file_name_in_a_path_lights_up_at_the_word_it_starts()
    {
        Assert.Equal([(4, 3)], UIChooser.MatchSpans("src/App.tsx", "app"));
    }

    [Fact]
    public void Letters_in_a_few_pieces_light_up_piece_by_piece()
    {
        // Sa of Save then l of All isn't a word start, so it's S then Al, two pieces
        Assert.Equal([(0, 1), (5, 2)], UIChooser.MatchSpans("Save All", "sal"));
        Assert.True(UIChooser.Matches("Save All", "sal"));
    }

    [Fact]
    public void No_match_and_nothing_typed_light_up_nothing()
    {
        Assert.Empty(UIChooser.MatchSpans("Save All", "xyz"));
        Assert.Empty(UIChooser.MatchSpans("Save All", "   "));
        Assert.Empty(UIChooser.MatchSpans(string.Empty, "a"));
    }

    [Fact]
    public void Every_piece_lit_is_inside_the_text_and_what_was_typed()
    {
        string[] texts = ["File: Save All", "Selection: Move Line Up", "src/components/AppHeader.tsx", "Toggle Word Wrap"];
        string[] typed = ["sa", "mlu", "aph", "tww", "line", "s", "wrap"];
        foreach (string text in texts)
        {
            foreach (string query in typed)
            {
                var spans = UIChooser.MatchSpans(text, query);
                Assert.Equal(spans.Count > 0, UIChooser.Matches(text, query));
                Assert.All(spans, span => Assert.True(span.Start >= 0 && span.Length > 0 && span.Start + span.Length <= text.Length));
                Assert.True(spans.Sum(span => span.Length) <= query.Replace(" ", string.Empty).Length);
            }
        }
    }

    [Fact]
    public void The_ranking_order_is_what_it_was()
    {
        // Close and Close All before what only has the letters in a few pieces, Save before Save All between two as good
        Assert.Equal(0, UIChooser.Rank("File: Close   (Ctrl+W)", "close"));
        Assert.Equal(1, UIChooser.Rank("File: Revert and Close", "close"));
        Assert.Equal(2, UIChooser.Rank("Edit: Unclosed", "close"));
        Assert.Equal(3, UIChooser.Rank("Close: Thing", "close"));
        Assert.Equal(4, UIChooser.Rank("Show Some Lines", "ssl"));
        Assert.Equal(5, UIChooser.Rank("Edit: Trigger Completion", "tcom"));
    }

    [Fact]
    public void A_row_in_parts_is_known_by_the_text_it_was_written_as()
    {
        var save = new UIChoice("Save", () => { }) { Quiet = "File", Keys = "Ctrl+S", Icon = "save" };
        Assert.Equal("File: Save   (Ctrl+S)", save.Id);

        UIChoice old = UIChoice.Of("Go: Line 12\tlet x = 1;   (Ctrl+G)", () => { });
        Assert.Equal(("Go", "Line 12", "let x = 1;", "Ctrl+G"), (old.Quiet, old.Text, old.Detail, old.Keys));
        Assert.Equal("Go: Line 12\tlet x = 1;   (Ctrl+G)", old.Id);

        Assert.Equal("src/App.tsx", new UIChoice("App.tsx", () => { }) { Detail = "src", Id = "src/App.tsx" }.Id);
    }

    [Fact]
    public void Backups_and_pictures_go_after_code()
    {
        Assert.Equal(0, UIChooser.Leftover("src/App.tsx"));
        Assert.Equal(0, UIChooser.Leftover("notes.md"));
        Assert.Equal(1, UIChooser.Leftover("art/hero.png"));
        Assert.Equal(1, UIChooser.Leftover("bin/Hex.dll"));
        Assert.Equal(2, UIChooser.Leftover("src/App.tsx.orig"));
        Assert.Equal(2, UIChooser.Leftover("main.cs~"));
        Assert.Equal(2, UIChooser.Leftover("old.bak"));
    }
}
