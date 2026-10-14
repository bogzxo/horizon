using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Preprocessing;

namespace Horizon.Tests;

public class HidlPreprocessorTests
{
    [Fact]
    public void A_file_says_what_it_defines_without_being_run()
    {
        const string source = "// a layout\n#define layout\n#define version \"3\"\nlet x = compositor.label({});\n";

        var defines = HIDLPreprocessor.Read(source);
        Assert.True(HIDLPreprocessor.IsLayout(source));
        Assert.Equal("", defines["layout"]);
        Assert.Equal("3", defines["version"]);
        Assert.False(HIDLPreprocessor.IsLayout("let x = 1;"));
    }

    [Fact]
    public void The_runtime_keeps_what_was_defined_and_the_program_can_ask()
    {
        var runtime = new HIDLRuntime { Output = null };
        runtime.Define("debug");

        var (success, result) = runtime.Evaluate("#define mode fast\nlet a = defined(\"debug\");\nlet b = definition(\"mode\");\nlet c = defined(\"nope\");\na");
        Assert.True(success, result);
        Assert.Equal("true", result);
        Assert.True(runtime.IsDefined("mode"));
        Assert.Equal("fast", runtime.Defines["mode"]);

        Assert.Equal("fast", runtime.Evaluate("b").result);
        Assert.Equal("false", runtime.Evaluate("c").result);
    }

    [Fact]
    public void Only_the_branch_that_is_wanted_is_kept_and_the_lines_stay_where_they_were()
    {
        var defines = new Dictionary<string, string>();
        string kept = HIDLPreprocessor.Process("#define a\n#if a\nlet x = 1;\n#elif b\nlet x = 2;\n#else\nlet x = 3;\n#endif\n#if !a\nlet y = 1;\n#endif\nlet z = 0;", defines);
        string[] lines = kept.Split('\n');

        Assert.Equal(12, lines.Length);
        Assert.Equal("let x = 1;", lines[2]);
        Assert.Equal("", lines[4]);
        Assert.Equal("", lines[6]);
        Assert.Equal("", lines[9]);
        Assert.Equal("let z = 0;", lines[11]);
    }

    [Fact]
    public void A_directive_that_makes_no_sense_is_a_parse_error_on_its_line()
    {
        var error = Assert.Throws<ParseException>(() => HIDLPreprocessor.Process("let a = 1;\n#nonsense\n", []));
        Assert.Equal(2, error.Line);

        error = Assert.Throws<ParseException>(() => HIDLPreprocessor.Process("#if a\nlet a = 1;\n", []));
        Assert.Equal(1, error.Line);
    }

    [Fact]
    public void The_lexer_and_the_parser_step_over_directives()
    {
        Token[] tokens = Lexer.Tokenize("#define layout\nlet a = 1; // not # a directive\n");
        Assert.Equal(TokenType.Directive, tokens[0].Type);
        Assert.Equal("#define layout", tokens[0].Value);

        // A # anywhere but first on its line is no directive, and one that is first parses away
        new Parser().ProduceSyntaxTree(Lexer.Tokenize("#define layout\n  #if layout\nlet a = 1;\n  #endif\n"));
    }
}
