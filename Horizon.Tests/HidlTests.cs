using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Library;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;

namespace Horizon.Tests;

/// <summary>
/// The language, end to end. Every test runs a bit of source and looks at what comes out, so a change to the lexer,
/// the parser or the interpreter that breaks something a game script does shows up here first.
/// </summary>
public class HidlTests
{
    private static IRuntimeValue Run(string source, Action<HIDLRuntime>? setup = null)
    {
        HIDLRuntime runtime = new() { Output = null };
        setup?.Invoke(runtime);
        return runtime.Run(source);
    }

    private static float Number(string source) => Assert.IsType<NumberValue>(Run(source)).Value;

    private static string Text(string source) => Assert.IsType<StringValue>(Run(source)).Value;

    private static bool Bool(string source) => Assert.IsType<BooleanValue>(Run(source)).Value;

    /* Values and operators */

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 / 4", 2.5f)]
    [InlineData("7 % 3", 1)]
    [InlineData("-3 + 5", 2)]
    [InlineData("2 * -3", -6)]
    [InlineData(".5 + .25", 0.75f)]
    public void Arithmetic(string source, float expected) => Assert.Equal(expected, Number(source));

    [Theory]
    [InlineData("1 < 2", true)]
    [InlineData("2 <= 2", true)]
    [InlineData("3 > 4", false)]
    [InlineData("3 >= 4", false)]
    [InlineData("1 == 1", true)]
    [InlineData("1 != 1", false)]
    [InlineData("\"a\" == \"a\"", true)]
    [InlineData("\"a\" != \"b\"", true)]
    [InlineData("null == null", true)]
    [InlineData("vec(1, 2) == vec(1, 2)", true)]
    [InlineData("!true", false)]
    [InlineData("!0", true)]
    public void Comparisons(string source, bool expected) => Assert.Equal(expected, Bool(source));

    [Fact]
    public void LogicalOperatorsShortCircuit()
    {
        // The right side of && is never looked at when the left is false, or it would blow up on the null
        Assert.False(Bool("let x = null; false && x.missing"));
        Assert.True(Bool("let x = null; true || x.missing"));
        Assert.Equal(3, Number("null || 3"));
        Assert.Equal(0, Number("0 && 5"));
    }

    [Fact]
    public void TextsJoinWithPlus()
    {
        Assert.Equal("round 2", Text("\"round \" + 2"));
        Assert.Equal("1-2", Text("1 + \"-\" + 2"));
        Assert.Equal("ab", Text("\"a\" + \"b\""));
        Assert.Equal("x: vec(1, 2)", Text("\"x: \" + vec(1, 2)"));
    }

    [Fact]
    public void TextEscapes()
    {
        Assert.Equal("say \"hi\"\n", Text("\"say \\\"hi\\\"\\n\""));
        Assert.Equal("a\\b", Text("\"a\\\\b\""));
    }

    [Fact]
    public void VectorsDoMaths()
    {
        var sum = Assert.IsType<Vector2Value>(Run("vec(1, 2) + vec(3, 4)"));
        Assert.Equal(new System.Numerics.Vector2(4, 6), sum.Value);

        var scaled = Assert.IsType<Vector3Value>(Run("vec(1, 2, 3) * 2"));
        Assert.Equal(new System.Numerics.Vector3(2, 4, 6), scaled.Value);

        var negated = Assert.IsType<Vector2Value>(Run("-vec(1, 2)"));
        Assert.Equal(new System.Numerics.Vector2(-1, -2), negated.Value);

        Assert.Equal(2, Number("vec(1, 2).y"));
        Assert.Equal(5, Number("vec(3, 4).length"));
        Assert.Equal(8, Number("vec(8).x + 0"));

        // A vector inside of a vector hands its numbers over
        var widened = Assert.IsType<Vector4Value>(Run("vec(vec(1, 2, 3), 4)"));
        Assert.Equal(new System.Numerics.Vector4(1, 2, 3, 4), widened.Value);
    }

    [Fact]
    public void VectorComponentsCanBeAssigned()
    {
        var moved = Assert.IsType<Vector2Value>(Run("let p = vec(1, 2); p.x = 10; p"));
        Assert.Equal(new System.Numerics.Vector2(10, 2), moved.Value);

        var nested = Assert.IsType<Vector2Value>(Run("let o = { pos: vec(1, 2) }; o.pos.y += 5; o.pos"));
        Assert.Equal(new System.Numerics.Vector2(1, 7), nested.Value);
    }

    /* Variables and assignment */

    [Fact]
    public void VariablesAndConstants()
    {
        Assert.Equal(3, Number("let x = 1; x = 3; x"));
        Assert.Equal(6, Number("let x = 1; x += 5; x"));
        Assert.Equal(8, Number("let x = 2; x *= 4; x"));
        Assert.Equal(1, Number("let x = 7; x %= 3; x"));
        Assert.Throws<HidlRuntimeException>(() => Run("const x = 1; x = 2;"));
        Assert.Throws<HidlRuntimeException>(() => Run("y = 2;"));
        Assert.Throws<HidlRuntimeException>(() => Run("let x = 1; let x = 2;"));
    }

    [Fact]
    public void AssignmentGoesToTheScopeThatDeclared()
    {
        // The old interpreter put the new value in the inner scope and the outer variable never changed
        Assert.Equal(5, Number("let x = 1; func set() { x = 5; } set(); x"));
        Assert.Equal(3, Number("let n = 0; for (i in 3) { n += 1; } n"));
    }

    [Fact]
    public void SemicolonsAreOptional() => Assert.Equal(3, Number("let a = 1\nlet b = 2\na + b"));

    [Fact]
    public void CommentsGoAnywhere()
    {
        Assert.Equal(3, Number("// start\nlet a = 1; /* middle */ let b = 2; // end\na + b"));
        Assert.Equal(2, Number("let o = {\n    // a comment inside of an object\n    a: 1, // after a property\n    b: 2,\n}; o.b"));
        Assert.Equal(3, Number("let l = [1, /* two */ 2, 3,]; l[2]"));
    }

    /* Functions */

    [Fact]
    public void FunctionsReturn()
    {
        Assert.Equal(6, Number("func double(x) { return x * 2; } double(3)"));
        Assert.Equal(1, Number("func pick(x) { if (x > 0) { return 1; } return -1; } pick(5)"));
        Assert.Equal(-1, Number("func pick(x) { if (x > 0) { return 1; } return -1; } pick(-5)"));

        // Without a return the last value is what comes out, and a bare return hands null back
        Assert.Equal(4, Number("func last(x) { x + 1 } last(3)"));
        Assert.IsType<NullValue>(Run("func nothing() { return; } nothing()"));
    }

    [Fact]
    public void ReturnLeavesLoopsAndNestedBlocks()
    {
        Assert.Equal(3, Number("func find(limit) { let i = 0; while (true) { if (i == limit) { return i; } i += 1; } } find(3)"));
        Assert.Equal(2, Number("func first_even(list) { for (x in list) { if (x % 2 == 0) { return x; } } return null; } first_even([1, 3, 2, 4])"));
    }

    [Fact]
    public void MissingArgumentsAreNull()
    {
        Assert.True(Bool("func f(a, b) { return b == null; } f(1)"));
        Assert.Equal(1, Number("func f(a) { return a; } f(1, 2, 3)"));
    }

    [Fact]
    public void AnonymousFunctionsClose()
    {
        Assert.Equal(15, Number("func adder(n) { return func(x) { return x + n; }; } let add5 = adder(5); add5(10)"));
        Assert.Equal(3, Number("let count = 0; let bump = func() { count += 1; }; bump(); bump(); bump(); count"));
    }

    [Fact]
    public void CallingNotAFunctionSays()
    {
        var error = Assert.Throws<HidlRuntimeException>(() => Run("let x = 5; x()"));
        Assert.Contains("'x' isn't a function", error.Message);

        error = Assert.Throws<HidlRuntimeException>(() => Run("let o = {}; o.go()"));
        Assert.Contains("'o.go' isn't a function", error.Message);
    }

    [Fact]
    public void NativeFunctionsGetCheckedArguments()
    {
        var error = Assert.Throws<HidlRuntimeException>(() => Run("math.pow(\"x\", 2)"));
        Assert.Contains("pow", error.Message);
        Assert.Contains("argument 1 has to be a number, not text", error.Message);

        error = Assert.Throws<HidlRuntimeException>(() => Run("math.pow(5)"));
        Assert.Contains("argument 2 is missing", error.Message);
    }

    /* Branching and loops */

    [Fact]
    public void IfElse()
    {
        const string grade = "func grade(x) { if (x > 10) { return \"big\"; } else if (x > 5) { return \"mid\"; } else { return \"small\"; } }";
        Assert.Equal("big", Text(grade + " grade(11)"));
        Assert.Equal("mid", Text(grade + " grade(7)"));
        Assert.Equal("small", Text(grade + " grade(1)"));
    }

    [Fact]
    public void Conditional()
    {
        Assert.Equal("big", Text("let x = 7; x > 5 ? \"big\" : \"small\""));
        Assert.Equal("small", Text("let x = 2; x > 5 ? \"big\" : \"small\""));
        Assert.Equal(2, Number("true ? false ? 1 : 2 : 3 + 0 == 3 ? 3 : 4"));
        Assert.Equal(4, Number("false ? 1 : 3 + 0 == 2 ? 3 : 4"));

        // Only the side that is picked is looked at
        Assert.Equal(1, Number("let o = null; true ? 1 : o.missing"));
    }

    [Fact]
    public void Truthiness()
    {
        Assert.Equal(1, Number("if ([1]) { 1 } else { 0 }"));
        Assert.Equal(0, Number("if ([]) { 1 } else { 0 }"));
        Assert.Equal(0, Number("if (\"\") { 1 } else { 0 }"));
        Assert.Equal(0, Number("if (null) { 1 } else { 0 }"));
        Assert.Equal(1, Number("if ({}) { 1 } else { 0 }"));
    }

    [Fact]
    public void WhileWithBreakAndContinue()
    {
        Assert.Equal(5, Number("let i = 0; while (true) { i += 1; if (i == 5) { break; } } i"));
        Assert.Equal(12, Number("let i = 0; let sum = 0; while (i < 6) { i += 1; if (i % 2 == 1) { continue; } sum += i; } sum"));
        Assert.Equal(3, Number("let i = 0; do { i += 1; } while (i < 3); i"));
    }

    [Fact]
    public void ForInWalksEverything()
    {
        Assert.Equal(6, Number("let sum = 0; for (x in [1, 2, 3]) { sum += x; } sum"));
        Assert.Equal(3, Number("let n = 0; for (i in 3) { n += 1; } n"));
        Assert.Equal("abc", Text("let out = \"\"; for (c in \"abc\") { out += c; } out"));
        Assert.Equal("a,b", Text("let keys = []; for (k in { a: 1, b: 2 }) { keys.push(k); } keys.join(\",\")"));
        Assert.Equal(2, Number("let n = 0; for (x in [1, 2, 3, 4]) { if (x == 3) { break; } n += 1; } n"));
    }

    [Fact]
    public void EachTurnOfAForHasItsOwnVariable()
    {
        // A function made in a loop keeps the value of its turn, not the last one
        Assert.Equal("0,1,2", Text("let fs = []; for (i in 3) { fs.push(func() { return i; }); } [fs[0](), fs[1](), fs[2]()].join(\",\")"));
    }

    [Fact]
    public void RunawayLoopsAreStopped()
    {
        var error = Assert.Throws<HidlRuntimeException>(() => Run("while (true) { }"));
        Assert.Contains("showed no sign of stopping", error.Message);
    }

    /* Objects and lists */

    [Fact]
    public void ObjectsAndMembers()
    {
        Assert.Equal(2, Number("let o = { a: 1, b: 2 }; o.b"));
        Assert.Equal(2, Number("let o = { a: 1, b: 2 }; o[\"b\"]"));
        Assert.Equal(5, Number("let o = {}; o.x = 5; o.x"));
        Assert.Equal(3, Number("let o = { n: { m: 1 } }; o.n.m += 2; o.n.m"));
        Assert.IsType<NullValue>(Run("let o = {}; o.missing"));
        Assert.Equal(1, Number("let x = 1; let o = { x }; o.x"));
        Assert.Equal(1, Number("let o = { \"with space\": 1 }; o[\"with space\"]"));
    }

    [Fact]
    public void ObjectSpread()
    {
        Assert.Equal(3, Number("let a = { x: 1, y: 2 }; let b = { ...a, y: 3 }; b.y"));
        Assert.Equal(1, Number("let a = { x: 1, y: 2 }; let b = { ...a, y: 3 }; b.x"));
        Assert.Equal(2, Number("let a = { x: 1, y: 2 }; let b = { y: 3, ...a }; b.y"));
    }

    [Fact]
    public void Lists()
    {
        Assert.Equal(3, Number("[1, 2, 3].count"));
        Assert.Equal(2, Number("[1, 2, 3][1]"));
        Assert.Equal(3, Number("[1, 2, 3][-1]"));
        Assert.Equal(9, Number("let l = [1, 2, 3]; l[1] = 9; l[1]"));
        Assert.Equal(4, Number("let l = [1, 2, 3]; l.push(4); l.last"));
        Assert.Equal(3, Number("let l = [1, 2, 3]; l.pop()"));
        Assert.Equal("1, 2, 3, 4", Text("([1, 2] + [3, 4]).join()"));
        Assert.Equal("1, 2, 3, 4", Text("[1, ...[2, 3], 4].join()"));
        Assert.True(Bool("[1, 2, 3].contains(2)"));
        Assert.Equal(1, Number("[\"a\", \"b\"].index_of(\"b\")"));
        Assert.Equal("3, 2, 1", Text("[1, 2, 3].reverse().join()"));
        Assert.Equal("1, 3", Text("[1, 2, 3].filter(func(x) { return x != 2; }).join()"));
        Assert.Equal("2, 4, 6", Text("[1, 2, 3].map(func(x) { return x * 2; }).join()"));
        Assert.Equal("1, 2, 3", Text("[3, 1, 2].sort().join()"));
        Assert.Equal("bbb, cc, a", Text("[\"a\", \"bbb\", \"cc\"].sort(func(s) { return -s.length; }).join()"));
        Assert.Equal("2, 3", Text("[1, 2, 3, 4].slice(1, 3).join()"));
        Assert.True(Bool("[1, 2, 3].any(func(x) { return x > 2; })"));
        Assert.False(Bool("[1, 2, 3].all(func(x) { return x > 2; })"));
    }

    [Fact]
    public void ListsAreShared()
    {
        Assert.Equal(2, Number("let a = [1]; let b = a; b.push(2); a.count"));
        Assert.Equal(1, Number("let a = [1]; let b = a.copy(); b.push(2); a.count"));
    }

    [Fact]
    public void ListIndexOutOfRangeSays()
    {
        var error = Assert.Throws<HidlRuntimeException>(() => Run("[1, 2][5]"));
        Assert.Contains("no item 5 in a list of 2", error.Message);
    }

    [Fact]
    public void TextMembers()
    {
        Assert.Equal(5, Number("\"hello\".length"));
        Assert.Equal("HELLO", Text("\"hello\".upper()"));
        Assert.Equal("a, b, c", Text("\"a,b, c\".split(\",\").join()"));
        Assert.Equal("ell", Text("\"hello\".substring(1, 4)"));
        Assert.Equal("e", Text("\"hello\"[1]"));
        Assert.True(Bool("\"hello\".starts_with(\"he\")"));
        Assert.Equal("007", Text("\"7\".pad_left(3, \"0\")"));
        Assert.Equal("hallo", Text("\"hello\".replace(\"e\", \"a\")"));
    }

    /* The library */

    [Fact]
    public void MathLibrary()
    {
        Assert.Equal(3, Number("math.min(5, 3, 8)"));
        Assert.Equal(8, Number("math.max([5, 3, 8])"));
        Assert.Equal(1, Number("math.clamp(5, 0, 1)"));
        Assert.Equal(5, Number("math.lerp(0, 10, 0.5)"));
        Assert.Equal(3, Number("math.round(2.5)"));
        Assert.Equal(2.5f, Number("math.round(2.4567, 1)"));
        Assert.Equal(2, Number("math.floor(2.9)"));
        Assert.Equal(-1, Number("math.sign(-7)"));
        Assert.Equal(5, Number("math.distance(vec(0, 0), vec(3, 4))"));
        Assert.Equal(1, Number("math.normalize(vec(0, 5)).y"));
        Assert.Equal(2, Number("math.abs(vec(-2, 2)).x"));

        float random = Number("math.random(1, 7)");
        Assert.InRange(random, 1, 6);
        Assert.Equal(MathF.Floor(random), random);

        Assert.True(Bool("[1, 2, 3].contains(math.pick([1, 2, 3]))"));
    }

    [Fact]
    public void CoreFunctions()
    {
        Assert.Equal("a, b", Text("keys({ a: 1, b: 2 }).join()"));
        Assert.Equal("1, 2", Text("values({ a: 1, b: 2 }).join()"));
        Assert.True(Bool("has({ a: 1 }, \"a\")"));
        Assert.False(Bool("has([1, 2], 3)"));
        Assert.Equal("0, 1, 2", Text("range(3).join()"));
        Assert.Equal("2, 4", Text("range(2, 6, 2).join()"));
        Assert.Equal(3, Number("len([1, 2, 3])"));
        Assert.Equal("number", Text("typeof(1)"));
        Assert.Equal("list", Text("typeof([])"));
        Assert.Equal("function", Text("typeof(func() {})"));
        Assert.Equal(42, Number("number(\"42\")"));
        Assert.Equal(-1, Number("number(\"nope\", -1)"));
        Assert.Equal("3.14", Text("format(3.14159, 2)"));
        Assert.Equal("a 1 vec(1, 2)", Text("text(\"a\", \" \", 1, \" \", vec(1, 2))"));
    }

    [Fact]
    public void PrintGoesToOutput()
    {
        var lines = new List<string>();
        Run("print(\"round\", 2); print([1, 2])", runtime => runtime.Output = lines.Add);
        Assert.Equal(["round 2", "[1, 2]"], lines);
    }

    [Fact]
    public void ErrorStopsWithTheScriptsMessage()
    {
        var error = Assert.Throws<HidlRuntimeException>(() => Run("error(\"no such move\")"));
        Assert.Equal("no such move", error.Message);
    }

    [Fact]
    public void HostObjectsThroughNatives()
    {
        int health = 100;
        var result = Run("fight.hurt(30); fight.health", runtime =>
        {
            runtime.UserScope.DeclareSystem("fight", Natives.Object(
                ("health", Natives.Property(() => new NumberValue(health))),
                ("hurt", Natives.Action("hurt", args => health -= args.Integer(0)))));
        });

        Assert.Equal(70, Assert.IsType<NumberValue>(result).Value);
        Assert.Throws<HidlRuntimeException>(() => Run("fight = 1", runtime => runtime.UserScope.DeclareSystem("fight", Natives.Object())));
    }

    [Fact]
    public void IncludeRunsAnotherFile()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hidl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "shared.hor"), "let scale = 2; func twice(x) { return x * scale; }");
            File.WriteAllText(Path.Combine(folder, "loop.hor"), "include(\"loop.hor\")");

            HIDLRuntime runtime = new() { BaseDirectory = folder, Output = null };
            Assert.Equal(8, Assert.IsType<NumberValue>(runtime.Run("let shared = include(\"shared.hor\"); shared.twice(4)")).Value);

            var error = Assert.Throws<HidlRuntimeException>(() => runtime.Run("include(\"loop.hor\")"));
            Assert.Contains("includes itself", error.Message);

            error = Assert.Throws<HidlRuntimeException>(() => runtime.Run("include(\"missing.hor\")"));
            Assert.Contains("missing.hor", error.Message);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /* Errors */

    [Fact]
    public void ParseErrorsSayWhere()
    {
        var error = Assert.Throws<ParseException>(() => HIDLRuntime.Parse("let x = ;"));
        Assert.Equal(1, error.Line);
        Assert.Contains("';'", error.Message);

        error = Assert.Throws<ParseException>(() => HIDLRuntime.Parse("let o = {\n    a: 1\n    b: 2\n}"));
        Assert.Equal(3, error.Line);

        error = Assert.Throws<ParseException>(() => HIDLRuntime.Parse("\"unterminated"));
        Assert.Equal(1, error.Line);
    }

    [Fact]
    public void EvaluateHandsBackTheMessage()
    {
        HIDLRuntime runtime = new() { Output = null };
        var (ok, result) = runtime.Evaluate("let x = 1; x.y.z");
        Assert.False(ok);
        Assert.Contains("null", result);

        (ok, result) = runtime.Evaluate("1 + 2");
        Assert.True(ok);
        Assert.Equal("3", result);
    }

    [Fact]
    public void ReadingAMemberOfNullSays()
    {
        var error = Assert.Throws<HidlRuntimeException>(() => Run("let o = {}; o.a.b"));
        Assert.Contains("'b'", error.Message);
        Assert.Contains("null", error.Message);
    }

    /* Lenient lexing, for editors */

    [Fact]
    public void LenientLexingNeverThrows()
    {
        Token[] tokens = Lexer.Tokenize("let x = \"open\n@ 1", lenient: true);

        Assert.Equal(TokenType.TextLiteral, tokens[3].Type);
        Assert.Equal("open", tokens[3].Value);
        Assert.Equal(TokenType.Unknown, tokens[4].Type);
        Assert.Equal("@", tokens[4].Value);
        Assert.Equal(TokenType.Number, tokens[5].Type);
        Assert.Throws<ParseException>(() => Lexer.Tokenize("let x = \"open"));
    }

    [Fact]
    public void TokensKnowWhereTheyAre()
    {
        Token[] tokens = Lexer.Tokenize("let name = \"a\\\"b\"; // hi", lenient: true);

        Assert.Equal((0, 3), (tokens[0].Index, tokens[0].Length));
        Assert.Equal((4, 4), (tokens[1].Index, tokens[1].Length));

        // A text is as long as its source, escapes and quotes and all
        Assert.Equal((11, 6), (tokens[3].Index, tokens[3].Length));
        Assert.Equal(TokenType.Comment, tokens[5].Type);
        Assert.Equal(19, tokens[5].Index);
    }

    /* The writer */

    [Fact]
    public void WriterRoundTrips()
    {
        const string source = """
            let settings = {
                name: "say \"hi\"",
                volume: 0.75,
                "odd key": true,
                pos: vec(1.5, -2),
                tags: ["a", "b"],
                rows: [[1, 2], [3, 4]],
                nested: { empty: {}, none: null },
                tiny: 0.000001
            };
            """;

        HIDLRuntime runtime = new() { Output = null };
        runtime.Run(source);
        string written = runtime.ToText();

        HIDLRuntime again = new() { Output = null };
        again.Run(written);

        Assert.Equal(written, again.ToText());
        Assert.Equal("say \"hi\"", Assert.IsType<StringValue>(again.Run("settings.name")).Value);
        Assert.Equal(4, Assert.IsType<NumberValue>(again.Run("settings.rows[1][1]")).Value);
        Assert.Equal(0.000001f, Assert.IsType<NumberValue>(again.Run("settings.tiny")).Value);
        Assert.True(Assert.IsType<BooleanValue>(again.Run("settings[\"odd key\"]")).Value);
    }

    [Fact]
    public void WriterLeavesFunctionsOut()
    {
        HIDLRuntime runtime = new() { Output = null };
        runtime.Run("let o = { a: 1, go: func() {} }; func f() {}");
        string written = runtime.ToText();

        Assert.DoesNotContain("func", written);
        Assert.Contains("a: 1", written);
    }

    [Fact]
    public void ToTextInScripts()
    {
        Assert.Equal("[1, 2, 3]", Text("to_text([1, 2, 3])"));
        Assert.Equal("vec(1, 2)", Text("to_text(vec(1, 2))"));
    }
}
