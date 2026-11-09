using Horizon.HIDL;
using Horizon.HIDL.Runtime;

namespace Horizon.Tests;

/// <summary>
/// What texts and lists can do that the extensions used to write for themselves, format with a text, entries, and what
/// an error says when a name was spelt wrong.
/// </summary>
public class HidlLibraryTests
{
    private static IRuntimeValue Run(string source)
    {
        HIDLRuntime runtime = new() { Output = null };
        return runtime.Run(source);
    }

    private static float Number(string source) => Assert.IsType<NumberValue>(Run(source)).Value;

    private static string Text(string source) => Assert.IsType<StringValue>(Run(source)).Value;

    private static bool Bool(string source) => Assert.IsType<BooleanValue>(Run(source)).Value;

    [Theory]
    [InlineData("\"  a  \".trim_start()", "a  ")]
    [InlineData("\"  a  \".trim_end()", "  a")]
    [InlineData("\"a,b\".last_index_of(\",\") + \"\"", "1")]
    [InlineData("text(\"abcabc\".count_of(\"bc\"))", "2")]
    [InlineData("\"a\\r\\nb\\n\\nc\".lines().join(\"|\")", "a|b||c")]
    [InlineData("\"hey\".chars().join(\"-\")", "h-e-y")]
    public void TextsDoMore(string source, string expected) => Assert.Equal(expected, Text(source));

    [Theory]
    [InlineData("\"7\".is_digit()", true)]
    [InlineData("\"7a\".is_digit()", false)]
    [InlineData("\"\".is_digit()", false)]
    [InlineData("\"Ab\".is_letter()", true)]
    [InlineData("\"AB\".is_upper()", true)]
    [InlineData("\"aB\".is_lower()", false)]
    [InlineData("\" \\t\".is_space()", true)]
    [InlineData("\"a_1\".is_word()", true)]
    [InlineData("\"a-1\".is_word()", false)]
    public void TextsSayWhatTheyAre(string source, bool expected) => Assert.Equal(expected, Bool(source));

    [Fact]
    public void ListsDoMore()
    {
        Assert.Equal(3, Number("[1, 3, 5].find(x => x > 2)"));
        Assert.True(Bool("[1, 3, 5].find(x => x > 9) == null"));
        Assert.Equal(1, Number("[1, 3, 5].find_index(x => x > 2)"));
        Assert.Equal(-1, Number("[1, 3, 5].find_index(x => x > 9)"));
        Assert.Equal(9, Number("[1, 3, 5].reduce((a, b) => a + b)"));
        Assert.Equal(19, Number("[1, 3, 5].reduce((a, b) => a + b, 10)"));
        Assert.Equal(9, Number("[1, 3, 5].sum()"));
        Assert.Equal(18, Number("[1, 3, 5].sum(x => x * 2)"));
        Assert.Equal("[1, 2, 3]", Text("text([1, 2, 1, 3, 2].unique())"));
        Assert.Equal("[1, 2, 3, 4]", Text("text([[1, 2], 3, [4]].flat())"));
        Assert.Equal(2, Number("[1, 2, 1].last_index_of(1)"));
        Assert.Equal(6, Number("let t = 0; [1, 2, 3].each(x => t += x); t"));
        Assert.Equal(1, Number("let l = [1, 2, 3]; l.remove_at(-1); l.remove_at(-1); l.count"));
    }

    [Fact]
    public void AMethodTakenAsAValueStillWorks()
    {
        Assert.Equal(2, Number("let l = []; let add = l.push; add(1); add(2); l.count"));
        Assert.Equal("HI", Text("let up = \"hi\".upper; up()"));
    }

    [Fact]
    public void FormatFillsInAText()
    {
        Assert.Equal("1 of 3", Text("format(\"{0} of {1}\", 1, 3)"));
        Assert.Equal("Blocky has 40", Text("format(\"{name} has {health}\", { name: \"Blocky\", health: 40 })"));
        Assert.Equal("{2} stays", Text("format(\"{2} stays\", 1)"));
        Assert.Equal("1.50", Text("format(1.5)"));
        Assert.Equal("1.5", Text("format(1.5, 1)"));
    }

    [Fact]
    public void EntriesArePairs()
    {
        Assert.Equal("[[a, 1], [b, 2]]", Text("text(entries({ a: 1, b: 2 }))"));
    }

    [Fact]
    public void AMisspeltNameSaysSo()
    {
        var error = Assert.Throws<HidlRuntimeException>(() => Run("func damage_of(m) { return 1; } damage_off(2)"));
        Assert.Contains("There is no 'damage_off'", error.Message);
        Assert.Contains("Did you mean damage_of?", error.Message);

        error = Assert.Throws<HidlRuntimeException>(() => Run("let state = { a: 1 }; stat.a"));
        Assert.Contains("There is no 'stat'", error.Message);
        Assert.Contains("Did you mean state?", error.Message);

        error = Assert.Throws<HidlRuntimeException>(() => Run("[1].pussh(2)"));
        Assert.Contains("Did you mean push?", error.Message);

        // a name that is there and null is still null, the message says so
        error = Assert.Throws<HidlRuntimeException>(() => Run("let x = null; x.y"));
        Assert.Contains("the value is null", error.Message);
    }
}
