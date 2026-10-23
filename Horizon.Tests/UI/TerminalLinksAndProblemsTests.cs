using Horizon.UI.Components;

namespace Horizon.Tests.UI;

/// <summary>Links found in a terminal's text, and problems read off a program's output by the matchers, without a terminal.</summary>
public class TerminalLinksAndProblemsTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "repo");

    private static bool There(string path) => path.Replace('\\', '/').EndsWith("/repo/src/Program.cs") || path.Replace('\\', '/').EndsWith("/repo/game.py")
        || path.Replace('\\', '/').EndsWith("/repo/src/main.rs") || path.Replace('\\', '/').EndsWith("/repo/shaders/lit.slang");

    [Theory]
    [InlineData("src/Program.cs:42", 42, 0)]
    [InlineData("  at Run() src/Program.cs:42:7 here", 42, 7)]
    [InlineData("src/Program.cs(42,7): error CS1002: ; expected", 42, 7)]
    [InlineData("src/Program.cs(42)", 42, 0)]
    public void A_file_with_a_line_after_it_is_a_link(string line, int number, int column)
    {
        TerminalLink link = Assert.Single(TerminalLinks.Find(line, Folder, There));
        Assert.Equal(number, link.Line);
        Assert.Equal(column, link.Column);
        Assert.EndsWith("Program.cs", link.File);
        Assert.Equal("src/Program.cs", line[link.From..(link.From + "src/Program.cs".Length)]);
    }

    [Fact]
    public void The_ways_runtimes_say_a_place_are_links()
    {
        Assert.Equal(3, TerminalLinks.Find("  File \"game.py\", line 3, in <module>", Folder, There).Single().Line);
        string stack = $"   at Game.Main() in {Path.Combine(Folder, "src", "Program.cs")}:line 12";
        Assert.Equal(12, TerminalLinks.Find(stack, Folder, There).Single().Line);
    }

    [Fact]
    public void A_url_is_a_link_and_a_file_that_isnt_there_isnt()
    {
        var found = TerminalLinks.Find("see https://example.com/docs/page?x=1, or missing.cs:3", Folder, There);
        TerminalLink url = Assert.Single(found);
        Assert.True(url.IsUrl);
        Assert.Equal("https://example.com/docs/page?x=1", url.Target);

        Assert.Equal("https://example.com/docs/page?x=1", TerminalLinks.At("see https://example.com/docs/page?x=1", 8, Folder, There)?.Target);
        Assert.Null(TerminalLinks.At("see https://example.com/docs/page?x=1", 1, Folder, There));
    }

    private static List<TerminalProblem> Read(string output)
    {
        var problems = new List<TerminalProblem>();
        var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add);
        reader.Feed(output);
        return problems;
    }

    [Fact]
    public void Dotnet_errors_and_warnings_are_read()
    {
        var problems = Read(
            "\u001b[31mC:\\repo\\src\\Program.cs(12,5): error CS1002: ; expected [C:\\repo\\App.csproj]\u001b[0m\r\n" +
            "C:\\repo\\src\\Game.cs(3,1): warning CS0168: The variable 'x' is declared but never used [C:\\repo\\App.csproj]\r\n" +
            "Build FAILED.\r\n");

        Assert.Equal(2, problems.Count);
        Assert.Equal(("C:\\repo\\src\\Program.cs", 12, 5, "CS1002: ; expected", false), (problems[0].File, problems[0].Line, problems[0].Column, problems[0].Message, problems[0].Warning));
        Assert.True(problems[1].Warning);
        Assert.Equal("dotnet", problems[0].Matcher);
    }

    [Fact]
    public void A_python_traceback_is_read_where_it_happened()
    {
        var problems = Read("Traceback (most recent call last):\n  File \"main.py\", line 9, in <module>\n    run()\n  File \"game.py\", line 3, in run\n    1 / 0\nZeroDivisionError: division by zero\n");
        TerminalProblem problem = Assert.Single(problems);
        Assert.Equal(("game.py", 3, "ZeroDivisionError: division by zero"), (problem.File, problem.Line, problem.Message));
    }

    [Fact]
    public void Gcc_rust_and_slang_are_read()
    {
        var gcc = Read("src/main.c:7:12: error: expected ';' before '}' token\n");
        Assert.Equal(("src/main.c", 7, 12), (gcc.Single().File, gcc.Single().Line, gcc.Single().Column));

        var rust = Read("error[E0308]: mismatched types\n  --> src/main.rs:4:18\n   |\n");
        Assert.Equal(("src/main.rs", 4, 18, "E0308: mismatched types"), (rust.Single().File, rust.Single().Line, rust.Single().Column, rust.Single().Message));

        var slang = Read("shaders/lit.slang(31): error 30019: expected an expression of type 'float4'\n");
        Assert.Equal(("shaders/lit.slang", 31), (slang.Single().File, slang.Single().Line));
    }

    [Fact]
    public void D_says_where_with_a_caret_under_the_code_or_with_its_column()
    {
        var problems = Read(
            "source\\app.d(11): Error: cannot implicitly convert expression `\"text\"` of type `string` to `int`\n" +
            "    int wrong = \"text\";\n" +
            "                ^\n" +
            "main.d(4,8): Deprecation: `old` is deprecated\n" +
            "util.d(9): Error: undefined identifier `x`\n" +
            "    x++;\n" +
            "    ^\n" +
            "Error C:\\D\\dmd2\\windows\\bin64\\dmd.exe failed with exit code 1.\n");

        Assert.Equal(3, problems.Count);
        Assert.Equal(("source\\app.d", 11, 17, false), (problems[0].File, problems[0].Line, problems[0].Column, problems[0].Warning));
        Assert.Equal(("main.d", 4, 8, true), (problems[1].File, problems[1].Line, problems[1].Column, problems[1].Warning));
        Assert.Equal(("util.d", 9, 5), (problems[2].File, problems[2].Line, problems[2].Column));
    }

    [Fact]
    public void A_d_or_dotnet_program_stopped_by_an_exception_says_where_it_was_thrown()
    {
        var d = Read("core.exception.ArrayIndexError@util.d(15): index [5] is out of bounds for array of length 5\n----------------\n");
        Assert.Equal(("util.d", 15, "core.exception.ArrayIndexError: index [5] is out of bounds for array of length 5"), (d.Single().File, d.Single().Line, d.Single().Message));

        var dotnet = Read(
            "Unhandled exception. System.InvalidOperationException: Sequence contains no elements\n" +
            "   at System.Linq.ThrowHelper.ThrowNoElementsException()\n" +
            "   at Program.<Main>$(String[] args) in C:\\repo\\src\\Program.cs:line 7\n");
        Assert.Equal(("C:\\repo\\src\\Program.cs", 7, false), (dotnet.Single().File, dotnet.Single().Line, dotnet.Single().Warning));
        Assert.StartsWith("System.InvalidOperationException: ", dotnet.Single().Message);
    }

    [Fact]
    public void A_line_that_comes_in_pieces_is_read_once_it_is_whole()
    {
        var problems = new List<TerminalProblem>();
        var reader = new TerminalProblemReader(TerminalProblemMatcher.Shipped, problems.Add);
        reader.Feed("src/main.c:7:1");
        Assert.Empty(problems);
        reader.Feed("2: warning: unused variable\n");
        Assert.True(Assert.Single(problems).Warning);
    }

    [Fact]
    public void Plain_output_says_nothing()
    {
        Assert.Empty(Read("Restore complete (0.4s)\nHello, World!\nPS C:\\repo> dir\n  Directory: C:\\repo\n"));
    }

    private const string A = "\u001b]133;A\a", B = "\u001b]133;B\a", C = "\u001b]133;C\a";

    [Fact]
    public void An_error_typed_at_a_prompt_with_marks_is_no_error_and_what_the_command_writes_is()
    {
        // bash's prompt ends in $, which no pattern keeps out, the marks round it do
        string typed = $"{A}me@box:/repo$ {B}printf '%s\\n' '/repo/src/Program.cs(12,5): error CS1002: ; expected'\n";
        Assert.Empty(Read(typed));
        Assert.Single(Read(typed + $"{C}/repo/src/Program.cs(12,5): error CS1002: ; expected\n"));
    }

    [Theory]
    [InlineData("plain output", "plain output")]
    [InlineData(A + "PS C:\\> " + B + "dir", "")]
    [InlineData(B + "the end of a prompt from the line before", "")]
    [InlineData("an error, then the next prompt drawn on the same line" + A + "PS C:\\> " + B, "an error, then the next prompt drawn on the same line")]
    [InlineData(A + "$ " + B + "make" + C + "output after the mark", C + "output after the mark")]
    [InlineData("one" + A + "$ " + B + "a" + C + "two" + A + "$ " + B + "b", "one" + C + "two")]
    [InlineData(A + B + "C:\\hex\\Program.cs(12,5): error CS1002: ; expected", "C:\\hex\\Program.cs(12,5): error CS1002: ; expected")]
    public void A_line_loses_its_prompts_and_what_was_typed_at_them(string raw, string kept) =>
        Assert.Equal(kept, TerminalProblemReader.WithoutPrompts(raw));
}
