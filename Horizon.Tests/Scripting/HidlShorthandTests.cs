using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;

namespace Horizon.Tests;

/// <summary>
/// The shorthands of the language, arrows, bodies without braces, in, two names in a for, keys in brackets, delete of a
/// key, defaults, taking an object or a list apart, and ?. on an index or a call. Each one is checked against the long
/// way it stands in for, and the long way is checked to still mean what it meant.
/// </summary>
public class HidlShorthandTests
{
    private static IRuntimeValue Run(string source)
    {
        HIDLRuntime runtime = new() { Output = null };
        return runtime.Run(source);
    }

    private static float Number(string source) => Assert.IsType<NumberValue>(Run(source)).Value;

    private static string Text(string source) => Assert.IsType<StringValue>(Run(source)).Value;

    private static bool Bool(string source) => Assert.IsType<BooleanValue>(Run(source)).Value;

    /* Arrows */

    [Theory]
    [InlineData("let double = x => x * 2; double(4)", 8)]
    [InlineData("let add = (a, b) => a + b; add(2, 3)", 5)]
    [InlineData("let seven = () => 7; seven()", 7)]
    [InlineData("let f = (a) => { let b = a + 1; return b * 2; }; f(2)", 6)]
    [InlineData("[1, 2, 3].map(x => x * 10).reduce((total, x) => total + x, 0)", 60)]
    [InlineData("let adder = n => x => x + n; adder(5)(10)", 15)]
    [InlineData("let pick = x => x > 2 ? \"big\" : \"small\"; pick(3) == \"big\" ? 1 : 0", 1)]
    public void ArrowsAreFunctions(string source, float expected) => Assert.Equal(expected, Number(source));

    [Fact]
    public void AnArrowIsTheSameValueAsAFunction()
    {
        Assert.IsType<AnonymousFunctionValue>(Run("x => x"));
        Assert.Equal("function", Text("typeof(x => x)"));

        // A grouping bracket is still a grouping bracket
        Assert.Equal(9, Number("let a = 1; (a + 2) * 3"));
    }

    [Fact]
    public void AnArrowCloses()
    {
        Assert.Equal(3, Number("let count = 0; let bump = () => count += 1; bump(); bump(); bump(); count"));
    }

    /* Bodies without braces */

    [Fact]
    public void OneStatementNeedsNoBraces()
    {
        Assert.Equal(1, Number("func f(x) { if (x > 2) return 1; return 0; } f(3)"));
        Assert.Equal(0, Number("func f(x) { if (x > 2) return 1; else return 0; } f(1)"));
        Assert.Equal(2, Number("func f(x) { if (x == 1) return 1; else if (x == 2) return 2; else return 3; } f(2)"));
        Assert.Equal(10, Number("let total = 0; for (x in [1, 2, 3, 4]) total += x; total"));
        Assert.Equal(5, Number("let i = 0; while (i < 5) i += 1; i"));
        Assert.Equal(1, Number("let i = 0; do i += 1; while (i < 0); i"));
        Assert.Equal(3, Number("func f() { if (true) return; return 3; } f() ?? 3"));
    }

    [Fact]
    public void ALineAfterAnIfIsAStatementOfItsOwn()
    {
        // Not a call of what the if came to, nor an index into it, nor a sum with it
        Assert.Equal(3, Number("let l = [1, 2, 3];\nif (false) l = [];\n[1, 2].push(3);\nl.count"));
        Assert.Equal(-1, Number("let a = 1;\nif (true) a = 2;\n-1"));

        // and as a value it is still a value
        Assert.Equal(2, Number("let a = if (true) { 2 } else { 3 }; a"));
    }

    [Fact]
    public void AnElseGoesWithTheNearestIf()
    {
        Assert.Equal(2, Number("let r = 0; if (true) if (false) r = 1; else r = 2; r"));
    }

    [Fact]
    public void ABodyIsStillNeeded()
    {
        Assert.Throws<ParseException>(() => HIDLRuntime.Parse("if (true)"));
        Assert.Throws<ParseException>(() => HIDLRuntime.Parse("while (true) ;"));
    }

    /* in */

    [Theory]
    [InlineData("\"a\" in { a: 1 }", true)]
    [InlineData("\"b\" in { a: 1 }", false)]
    [InlineData("2 in [1, 2, 3]", true)]
    [InlineData("\"x\" in [\"a\", \"b\"]", false)]
    [InlineData("\"ell\" in \"hello\"", true)]
    [InlineData("\"a\" in null", false)]
    [InlineData("!(\"a\" in { b: 1 })", true)]
    [InlineData("let c = \" \"; c in [\" \", \"\\t\", \"\\n\"]", true)]
    [InlineData("1 + 1 in [2]", true)]
    public void InSaysWhetherItHasIt(string source, bool expected) => Assert.Equal(expected, Bool(source));

    [Fact]
    public void ForStillReadsItsOwnIn()
    {
        Assert.Equal(6, Number("let t = 0; for (x in [1, 2, 3]) { t += x; } t"));
        Assert.Equal(2, Number("let t = 0; for (let k in { a: 1, b: 2 }) { t += 1; } t"));
    }

    /* Two names in a for */

    [Fact]
    public void ForWithTwoNamesHasTheKeyOrThePlace()
    {
        Assert.Equal("a=1;b=2;", Text("let s = \"\"; for (k, v in { a: 1, b: 2 }) s += `{k}={v};`; s"));
        Assert.Equal("0x;1y;", Text("let s = \"\"; for (i, item in [\"x\", \"y\"]) s += `{i}{item};`; s"));
        Assert.Equal("0h1i", Text("let s = \"\"; for (i, c in \"hi\") s += `{i}{c}`; s"));
    }

    /* Keys in brackets, delete */

    [Fact]
    public void AKeyCanBeWorkedOut()
    {
        Assert.Equal(5, Number("let k = \"jab\"; let o = { [k]: 5 }; o.jab"));
        Assert.Equal(2, Number("let o = { [\"a\" + \"b\"]: 1, c: 2 }; len(o)"));
        Assert.Equal(1, Number("let o = { [1 + 1]: 1 }; o[\"2\"]"));
    }

    [Fact]
    public void DeleteTakesAKeyOrAnItemOut()
    {
        Assert.Equal(1, Number("let o = { a: 1, b: 2 }; delete o.b; len(o)"));
        Assert.Equal(1, Number("let o = { a: 1, b: 2 }; let k = \"a\"; delete o[k]; len(o)"));
        Assert.Equal("[1, 3]", Text("let l = [1, 2, 3]; delete l[1]; text(l)"));
        Assert.Equal("[1, 2]", Text("let l = [1, 2, 3]; delete l[-1]; text(l)"));

        // and a variable, as it always did
        Assert.True(Bool("let x = 1; delete x; x == null"));
    }

    /* Defaults */

    [Fact]
    public void AParameterCanHaveADefault()
    {
        Assert.Equal(12, Number("func f(a, b = 2) { return a * b; } f(6)"));
        Assert.Equal(18, Number("func f(a, b = 2) { return a * b; } f(6, 3)"));
        Assert.Equal(8, Number("func f(a, b = a * 2) { return b; } f(4)"));
        Assert.Equal(7, Number("let f = (a = 7) => a; f()"));
        Assert.Equal(7, Number("let f = func(a = 7) { return a; }; f(null)"));
    }

    /* Taking things apart */

    [Fact]
    public void AnObjectOrAListCanBeTakenApart()
    {
        Assert.Equal(3, Number("let { a, b } = { a: 1, b: 2 }; a + b"));
        Assert.True(Bool("let { a, missing } = { a: 1 }; missing == null"));
        Assert.Equal(5, Number("let [x, y] = [2, 3]; x + y"));
        Assert.Equal(4, Number("let [x, y] = vec(1, 3); x + y"));
        Assert.True(Bool("let [x, y, z] = [1]; z == null"));
        Assert.Throws<HidlRuntimeException>(() => Run("const { a } = { a: 1 }; a = 2"));
        Assert.Throws<HidlRuntimeException>(() => Run("let { a } = null;"));
    }

    /* ?. on an index and a call */

    [Fact]
    public void OptionalIndexAndCall()
    {
        Assert.True(Bool("let l = null; l?.[0] == null"));
        Assert.Equal(2, Number("let l = [1, 2]; l?.[1]"));
        Assert.True(Bool("let o = {}; o.go?.() == null"));
        Assert.Equal(4, Number("let o = { go: () => 4 }; o.go?.()"));

        // and a ? with a number after it is still a conditional
        Assert.Equal(0.5f, Number("true ?.5 : 1"));
    }

    /* The lexer */

    [Fact]
    public void AnArrowIsOneToken()
    {
        Token[] tokens = Lexer.Tokenize("x => x >= 1");
        Assert.Equal(TokenType.Arrow, tokens[1].Type);
        Assert.Equal(TokenType.BinaryOperation, tokens[3].Type);
    }

    [Fact]
    public void ACharacterCanBeWrittenByItsNumber()
    {
        Assert.Equal("\u001b[2J", Text("\"\\u001b[2J\""));
        Assert.Equal("é", Text("`\\u00e9`"));

        // and a \u without four hex digits after it is a u, as it always was
        Assert.Equal("u12", Text("\"\\u12\""));
    }

    [Theory]
    [InlineData("1234567", 1234567)]
    [InlineData("0", 0)]
    [InlineData("007", 7)]
    [InlineData("12345678", 12345678)]
    [InlineData("1.5", 1.5f)]
    public void NumbersReadTheSameEveryWay(string written, float expected) => Assert.Equal(expected, Lexer.Tokenize(written)[0].Number);
}
