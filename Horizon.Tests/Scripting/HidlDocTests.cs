using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;

namespace Horizon.Tests;

// Documentation comments, // @doc over what they document or at the end of its line
public class HidlDocTests
{
    private const string SOURCE =
        "// Greeting messages inspired by that stupid American movie...\n" +
        "// @doc The list of greetings\n" +
        "let welcome_greetings = [\"hi\"];\n" +
        "\n" +
        "// @doc not this one, there is a blank line\n" +
        "\n" +
        "let lonely = 1;\n" +
        "// @comment Two lines\n" +
        "// a plain comment between is fine\n" +
        "// @doc of documentation\n" +
        "func greet() { return 1; }\n" +
        "let jab = {\n" +
        "    damage: 5, // @doc how much of the bar it takes\n" +
        "    frames: 3\n" +
        "};\n";

    [Fact]
    public void A_doc_comment_documents_what_is_under_it_or_at_the_end_of_its_line()
    {
        var runtime = new HIDLRuntime { Output = null, Sources = new HIDLSourceMap() };
        var (success, result) = runtime.Evaluate(SOURCE);
        Assert.True(success, result);

        Assert.True(runtime.Sources.TryGetDeclaration("welcome_greetings", out var greetings));
        Assert.Equal("The list of greetings", greetings.Doc);

        Assert.True(runtime.Sources.TryGetDeclaration("lonely", out var lonely));
        Assert.Null(lonely.Doc);

        Assert.True(runtime.Sources.TryGetDeclaration("greet", out var greet));
        Assert.Equal("Two lines of documentation", greet.Doc);

        var jab = (ObjectValue)runtime.UserScope.Lookup("jab");
        Assert.True(runtime.Sources.TryGetKey(jab, "damage", out var damage));
        Assert.Equal("how much of the bar it takes", damage.Doc);
        Assert.True(runtime.Sources.TryGetKey(jab, "frames", out var frames));
        Assert.Null(frames.Doc);
    }

    [Fact]
    public void The_documented_names_of_a_file_are_read_without_running_it()
    {
        List<HidlDoc> docs = HIDLDocs.Read(Lexer.Tokenize(SOURCE, lenient: true));

        Assert.Equal(["welcome_greetings", "greet", "damage"], docs.Select(doc => doc.Name));
        Assert.Equal((3, 5), (docs[0].Line, docs[0].Column));
    }
}
