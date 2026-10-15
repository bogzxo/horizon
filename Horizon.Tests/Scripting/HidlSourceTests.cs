using Horizon.HIDL;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;

namespace Horizon.Tests;

// Where things were written, what an error says about it, and what keeps a program from taking its host down with it
public class HidlSourceTests
{
    [Fact]
    public void An_error_says_the_line_it_happened_on()
    {
        var runtime = new HIDLRuntime { Output = null };
        var (success, result) = runtime.Evaluate("let a = 1;\nlet b = {\n    c: error(\"no\")\n};");

        Assert.False(success);
        Assert.Contains("no", result);
        Assert.Contains("line 3", result);
    }

    [Fact]
    public void The_source_map_knows_where_every_key_item_and_declaration_was_written()
    {
        var runtime = new HIDLRuntime { Output = null, Sources = new HIDLSourceMap(), SourceFile = "moves.hor" };
        var (success, result) = runtime.Evaluate("let base = { jab: 1 };\nlet moves = {\n    ...base,\n    kick: [\n        3,\n        4\n    ]\n};");
        Assert.True(success, result);

        var moves = (ObjectValue)runtime.UserScope.Lookup("moves");
        Assert.True(runtime.Sources.TryGetKey(moves, "kick", out var kick));
        Assert.Equal((4, 5, "moves.hor"), (kick.Span.Line, kick.Span.Column, kick.File));

        // What a spread brought along is where it was written in the first place
        Assert.True(runtime.Sources.TryGetKey(moves, "jab", out var jab));
        Assert.Equal(1, jab.Span.Line);

        Assert.True(runtime.Sources.TryGetItem((ListValue)moves.Properties["kick"], 1, out var four));
        Assert.Equal(6, four.Span.Line);

        Assert.True(runtime.Sources.TryGetDeclaration("moves", out var declared));
        Assert.Equal((2, 5), (declared.Span.Line, declared.Span.Column));
    }

    [Fact]
    public void The_source_map_knows_where_a_value_was_written_and_whether_it_is_written_as_itself()
    {
        const string source = "let jab = {\n    damage: -3,\n    tint: vec(1, 0.5, 0, 1),\n    name: \"jab\",\n    reach: 2 * 4,\n    frames: [1, 2 + 1]\n};";
        var runtime = new HIDLRuntime { Output = null, Sources = new HIDLSourceMap() };
        var (success, result) = runtime.Evaluate(source);
        Assert.True(success, result);

        var jab = (ObjectValue)runtime.UserScope.Lookup("jab");
        string Written(SourceLocation at) => source.Substring(at.Value.Index, at.Value.Length);

        Assert.True(runtime.Sources.TryGetKey(jab, "damage", out var damage));
        Assert.Equal(("-3", true), (Written(damage), damage.Literal));

        Assert.True(runtime.Sources.TryGetKey(jab, "tint", out var tint));
        Assert.Equal(("vec(1, 0.5, 0, 1)", true), (Written(tint), tint.Literal));

        Assert.True(runtime.Sources.TryGetKey(jab, "name", out var name));
        Assert.Equal(("\"jab\"", true), (Written(name), name.Literal));

        // Worked out, shown and not edited as text
        Assert.True(runtime.Sources.TryGetKey(jab, "reach", out var reach));
        Assert.Equal(("2 * 4", false), (Written(reach), reach.Literal));

        var frames = (ListValue)jab.Properties["frames"];
        Assert.True(runtime.Sources.TryGetItem(frames, 1, out var second));
        Assert.Equal(("2 + 1", false), (Written(second), second.Literal));
        Assert.True(runtime.Sources.TryGetItem(frames, 0, out var first));
        Assert.True(first.Literal);
    }

    [Fact]
    public void A_function_that_calls_itself_for_ever_is_stopped_and_the_runtime_still_works()
    {
        var runtime = new HIDLRuntime { Output = null };
        var (success, result) = runtime.Evaluate("func down(n) { return down(n + 1); }\ndown(0);");

        Assert.False(success);
        Assert.Contains("deep", result);

        // Nothing was left half way down a call
        var (again, answer) = runtime.Evaluate("func add(a, b) { return a + b; }\nadd(2, 3)");
        Assert.True(again, answer);
        Assert.Equal("5", answer);
    }

    [Fact]
    public void A_program_can_be_stopped_from_outside()
    {
        var runtime = new HIDLRuntime { Output = null };
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        runtime.Interpreter.Cancellation = cancel.Token;

        var (success, result) = runtime.Evaluate("let i = 0;\nwhile (true) { i += 1; }");
        Assert.False(success);
        Assert.Contains("Stopped", result);
    }

    [Fact]
    public void Merge_puts_one_object_over_another_all_the_way_down()
    {
        var runtime = new HIDLRuntime { Output = null };
        var (success, result) = runtime.Evaluate(
            "let shared = { jab: { damage: 5, frames: [1, 2] }, kick: { damage: 8 } };\n" +
            "let own = { jab: { damage: 7 }, sweep: { damage: 9 } };\n" +
            "let moves = merge(shared, own);\n" +
            "text(moves.jab.damage) + \" \" + text(len(moves.jab.frames)) + \" \" + text(moves.kick.damage) + \" \" + text(moves.sweep.damage) + \" \" + text(shared.jab.damage)");

        Assert.True(success, result);
        Assert.Equal("7 2 8 9 5", result);
    }

    [Fact]
    public void A_null_is_stood_in_for_with_two_question_marks_and_a_member_of_one_is_null_with_a_question_dot()
    {
        var runtime = new HIDLRuntime { Output = null };
        var (success, result) = runtime.Evaluate(
            "let phase = { animation: \"jab\" };\n" +
            "let cancel = phase.cancel ?? 3;\n" +
            "let deep = phase.phases?.raise?.cancel ?? 9;\n" +
            "let kept = phase.animation ?? \"none\";\n" +
            "text(cancel) + \" \" + text(deep) + \" \" + kept");

        Assert.True(success, result);
        Assert.Equal("3 9 jab", result);

        var (failed, error) = runtime.Evaluate("phase.phases.raise");
        Assert.False(failed);
        Assert.Contains("?.", error);
    }

    [Fact]
    public void A_template_writes_what_is_in_its_braces_into_the_text()
    {
        var runtime = new HIDLRuntime { Output = null };
        var (success, result) = runtime.Evaluate("let name = \"Ryu\";\nlet health = 80;\n`{name} has {health - 30} left, {`{1 + 1}`} rounds \\{not this}`");

        Assert.True(success, result);
        Assert.Equal("Ryu has 50 left, 2 rounds {not this}", result);
    }

    [Fact]
    public void True_false_and_null_are_words_of_the_language_and_any_word_is_a_key()
    {
        var runtime = new HIDLRuntime { Output = null };
        var (success, result) = runtime.Evaluate("let o = { for: 1, null: 2, in: true };\nlet n = null;\no.for + o.null + (o.in ? 10 : 0) + (n == null ? 100 : 0)");

        Assert.True(success, result);
        Assert.Equal("113", result);
    }

    [Fact]
    public void A_parse_for_an_editor_goes_on_after_every_mistake()
    {
        var (program, errors) = HIDLRuntime.ParseAll("let a = ;\nlet b = 2;\nlet c = { x: };\nlet d = 4;\nfunc f( { }\nlet e = 5;");

        Assert.Equal(3, errors.Count);
        Assert.Equal([1, 3, 5], errors.Select(error => error.Line));

        // What did parse is there, b, d and e
        Assert.Equal(3, program.Body.Count);
    }
}
