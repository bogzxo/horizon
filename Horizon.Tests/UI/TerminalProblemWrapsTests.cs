using System.Text;
using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>Problems read off lines Windows' console broke at the terminal's edge, put back together the way the screen has them.</summary>
public class TerminalProblemWrapsTests
{
    private const string PATH = @"C:\Users\bogz\AppData\Local\Temp\scratch\repos\engine\Horizon.UI\Components\Controls\Settings\SettingsPage.Reveal.cs";
    private const string MESSAGE = "'SettingsPage.Place' hides inherited member 'UIComponent.Place(UIRect)'. Use the new keyword if hiding was intended.";

    private static List<TerminalProblem> Read(string output, int width)
    {
        var problems = new List<TerminalProblem>();
        var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add, () => width);
        reader.Feed(output);
        return problems;
    }

    // A line drawn the way ConPTY draws one longer than the terminal, a row at a time, each after the first started by
    // putting the cursor back on the edge of the row before and writing its last letter again. Colours count for nothing
    private static string AsConsoleDraws(string line, int width)
    {
        var rows = new List<StringBuilder> { new() };
        int drawn = 0;
        for (int at = 0; at < line.Length; at++)
        {
            if (line[at] == '\u001b')
            {
                int end = at + 2;
                while (line[end] is not (>= '@' and <= '~'))
                    end++;
                rows[^1].Append(line, at, end - at + 1);
                at = end;
                continue;
            }

            if (drawn == width)
            {
                rows.Add(new StringBuilder());
                drawn = 0;
            }

            rows[^1].Append(line[at]);
            drawn++;
        }

        var said = new StringBuilder(rows[0].ToString());
        for (int row = 1; row < rows.Count; row++)
            said.Append($"\r\n\u001b[5;{width}H").Append(LastLetter(rows[row - 1].ToString())).Append(rows[row]);
        return said.Append("\r\n").ToString();
    }

    private static char LastLetter(string row)
    {
        string plain = System.Text.RegularExpressions.Regex.Replace(row, @"\x1B\[[0-9;]*[@-~]", string.Empty);
        return plain[^1];
    }

    [Theory]
    [InlineData(40)]
    [InlineData(127)]
    [InlineData(200)]
    public void A_warning_broken_at_the_edge_is_read_whole(int width)
    {
        string line = $"    {PATH}(97,56): warning CS0108: {MESSAGE}";
        TerminalProblem problem = Assert.Single(Read(AsConsoleDraws(line, width), width));

        Assert.Equal(PATH, problem.File);
        Assert.Equal((97, 56), (problem.Line, problem.Column));
        Assert.Equal("CS0108: " + MESSAGE, problem.Message);
        Assert.True(problem.Warning);
    }

    [Fact]
    public void A_coloured_error_broken_at_the_edge_is_read_whole()
    {
        string line = $"    {PATH[..^22]}\u001b[1m{PATH[^22..]}\u001b[22m(130,47): \u001b[31m\u001b[1merror\u001b[m \u001b[31m\u001b[1mCS0029\u001b[m: Cannot implicitly convert type 'string' to 'int'\u001b[K";
        var problems = Read(AsConsoleDraws(line, 60) + AsConsoleDraws($"    {PATH}(1,2): warning CS8602: Dereference of a possibly null reference.", 60), 60);

        Assert.Equal(2, problems.Count);
        Assert.Equal((PATH, 130, "CS0029: Cannot implicitly convert type 'string' to 'int'", false), (problems[0].File, problems[0].Line, problems[0].Message, problems[0].Warning));
        Assert.Equal("CS8602: Dereference of a possibly null reference.", problems[1].Message);
    }

    [Fact]
    public void A_line_broken_before_a_space_is_read_whole()
    {
        // What a build in a terminal 180 wide came out as, the colon written again on the edge, a new line, the rest
        string first = $"    {PATH}(130,47):";
        int width = first.Length;
        string said = first + $"\r\n\u001b[12;{width}H:\r\n \u001b[31m\u001b[1merror\u001b[m \u001b[31m\u001b[1mCS0029\u001b[m: Cannot implicitly convert type 'string' to 'int'\u001b[K\r\n";

        TerminalProblem problem = Assert.Single(Read(said, width));
        Assert.Equal((PATH, 130, 47, false), (problem.File, problem.Line, problem.Column, problem.Warning));
        Assert.Equal("CS0029: Cannot implicitly convert type 'string' to 'int'", problem.Message);
    }

    [Fact]
    public void A_line_broken_in_a_coloured_word_is_read_whole()
    {
        // The colour said again before the cursor goes back to the edge
        string first = $"    {PATH}(48,6): \u001b[33m\u001b[1mwarn";
        int width = $"    {PATH}(48,6): warn".Length;
        string said = first + $"\u001b[m\r\n\u001b[33m\u001b[1m\u001b[12;{width}Hning\u001b[m \u001b[33m\u001b[1mCA2255\u001b[m: The 'ModuleInitializer' attribute is only intended for apps\r\n";

        TerminalProblem problem = Assert.Single(Read(said, width));
        Assert.Equal((PATH, 48, true), (problem.File, problem.Line, problem.Warning));
        Assert.Equal("CA2255: The 'ModuleInitializer' attribute is only intended for apps", problem.Message);
    }

    [Fact]
    public void A_warning_drawn_over_a_progress_line_is_read()
    {
        // The progress of the build on a row, the cursor put back at the start of it, and the warning drawn over it
        string said = "  Horizon.UI net11.0 CoreCompile (1,2s)\u001b[12;1H" + @"C:\repo\A.cs(3,1): warning CS0168: never used" + "\u001b[K\r\n";
        TerminalProblem problem = Assert.Single(Read(said, 120));
        Assert.Equal((@"C:\repo\A.cs", "CS0168: never used"), (problem.File, problem.Message));

        // The same with a carriage return on its own, a line drawn over from its start
        problem = Assert.Single(Read("Restoring 40%\rRestoring 80%\r" + @"C:\repo\A.cs(3,1): error CS1002: ; expected" + "\r\n", 120));
        Assert.Equal((@"C:\repo\A.cs", false), (problem.File, problem.Warning));

        // Two carriage returns before the new line end it all the same
        Assert.Single(Read(@"C:\repo\A.cs(3,1): error CS1002: ; expected" + "\r\r\n", 120));
    }

    [Fact]
    public void A_package_warning_of_a_project_with_a_whole_path_is_read()
    {
        string project = @"C:\repo\Horizon.HIDL\Horizon.HIDL.csproj";
        TerminalProblem problem = Assert.Single(Read($"    {project} : warning NU1903: Package 'X' 1.0 has a known high severity vulnerability\r\n", 0));

        Assert.Equal(project, problem.File);
        Assert.Equal("NU1903: Package 'X' 1.0 has a known high severity vulnerability", problem.Message);
        Assert.True(problem.Warning);
    }

    [Fact]
    public void A_line_as_wide_as_the_terminal_and_the_next_are_two_lines()
    {
        string first = @"C:\repo\Program.cs(1,1): error CS1002: ; expected".PadRight(60, '!');
        var problems = Read(first + "\r\n" + @"C:\repo\Game.cs(3,1): warning CS0168: never used" + "\r\n", 60);

        Assert.Equal(2, problems.Count);
        Assert.Equal(@"C:\repo\Game.cs", problems[1].File);
    }

    [Fact]
    public void A_path_broken_onto_the_line_before_is_put_back_together_when_that_is_a_file()
    {
        string project = @"C:\repo\Horizon.HIDL\Horizon.HIDL.csproj";
        var problems = new List<TerminalProblem>();
        var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add, exists: file => file == project);
        reader.Feed("    C:\\repo\\Horizon.HI\r\nDL\\Horizon.HIDL.csproj : warning NU1903: Package 'X' 1.0 has a known high severity vulnerability\r\n");
        reader.Feed("Building...\r\nnowhere\\Thing.csproj : warning NU1903: Package 'X' 1.0 has a known high severity vulnerability\r\n");

        Assert.Equal([project, @"nowhere\Thing.csproj"], problems.Select(problem => problem.File));
    }

    [Fact]
    public void A_line_held_at_the_edge_is_read_when_the_prompt_comes()
    {
        string line = @"C:\repo\Program.cs(1,1): error CS1002: ; expected".PadRight(60, '!');
        Assert.Empty(Read(line + "\r\n", 60));
        Assert.Single(Read(line + "\r\n\u001b]133;A\aPS " + @"C:\repo> ", 60));
    }
}
