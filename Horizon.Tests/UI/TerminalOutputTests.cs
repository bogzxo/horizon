using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>What programs write coloured by rules, the shipped ones, a plugin's, and the view laying them over plain cells only.</summary>
public class TerminalOutputTests
{
    private static string Codes(string line, TerminalOutputHighlighter? highlighter = null)
    {
        var codes = new byte[line.Length];
        (highlighter ?? new TerminalOutputHighlighter()).Colour(line, codes);
        return new string(codes.Select(code =>
        {
            char letter = (TerminalOutputColour)(code & 0x0F) switch
            {
                TerminalOutputColour.Red => 'r', TerminalOutputColour.Green => 'g', TerminalOutputColour.Yellow => 'y',
                TerminalOutputColour.Blue => 'b', TerminalOutputColour.Magenta => 'm', TerminalOutputColour.Cyan => 'c',
                TerminalOutputColour.Dim => 'd', _ => '.'
            };
            return (code & TerminalOutputRule.UNDERLINE) != 0 ? char.ToUpperInvariant(letter) : letter;
        }).ToArray());
    }

    private static string Under(string line, string word, TerminalOutputHighlighter? highlighter = null) =>
        Codes(line, highlighter).Substring(line.IndexOf(word, StringComparison.Ordinal), word.Length);

    [Fact]
    public void Errors_warnings_and_what_went_well_have_their_colours()
    {
        Assert.Equal("rrrrr", Under("build Error in 3 files", "Error"));
        Assert.Equal("yyyyyyy", Under("warning: unused", "warning"));
        Assert.Equal("gggggg", Under("tests passed", "passed"));
        Assert.Equal("m", Under("build Error in 3 files", "3"));
        Assert.Equal("mmmmm", Under("took 120ms", "120ms"));
    }

    [Fact]
    public void A_diff_is_coloured_by_the_line()
    {
        Assert.Equal("gggggggg", Codes("+ added!"));
        Assert.Equal("rrrrrrrr", Codes("- gone!!"));
        Assert.Equal("....", Codes("--- ")[..4]);
        Assert.Equal("ccccccccccccccc", Under("@@ -1,2 +1,3 @@ fn", "@@ -1,2 +1,3 @@"));
    }

    [Fact]
    public void Paths_and_urls_are_underlined_and_a_stack_frame_is_dim()
    {
        const string line = @"   at Thing.Do() in C:\src\Thing.cs:42";
        Assert.Equal("dd", Under(line, "at"));
        Assert.Equal("CCCCCCCCCCCCCCCCCC", Under(line, @"C:\src\Thing.cs:42"));
        Assert.Equal("BBBBBBBBBBBBBBBBBB", Under("see https://dusk.dev/x now", "https://dusk.dev/x"));
    }

    [Fact]
    public void A_json_key_is_coloured()
    {
        Assert.Equal("cccccc", Under("  \"name\": \"dusk\",", "\"name\""));
    }

    [Fact]
    public void A_plugins_rule_goes_over_the_shipped_ones()
    {
        var highlighter = new TerminalOutputHighlighter();
        int version = highlighter.Version;
        highlighter.Add(new TerminalOutputRule("ticket", @"\bDUSK-\d+\b", TerminalOutputColour.Yellow, underline: true));
        Assert.NotEqual(version, highlighter.Version);
        Assert.Equal("YYYYYYYY", Under("fixes DUSK-123 today", "DUSK-123", highlighter));
        Assert.Equal(TerminalOutputColour.Cyan, TerminalOutputRule.ColourOf("CYAN"));
        Assert.Equal(TerminalOutputColour.None, TerminalOutputRule.ColourOf("chartreuse"));
    }

    [Fact]
    public void A_view_colours_what_a_program_left_plain_and_nothing_on_the_alternate_screen()
    {
        var view = new TerminalView { OutputHighlighter = new TerminalOutputHighlighter() };
        view.Feed("one error\r\n\u001b[32mtwo error\u001b[0m\r\n");
        view.ColourOutput();
        Assert.Equal("....rrrrr", view.OutputCodesText(0));
        // the codes are there for the second row as well, the paint leaves them off where the program said a colour
        Assert.Equal("....rrrrr", view.OutputCodesText(1));

        view.Feed("\u001b[?1049h");
        view.ColourOutput();
        Assert.Equal(string.Empty, view.OutputCodesText(0));
    }
}
