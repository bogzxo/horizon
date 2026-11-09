using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class SignatureParameterTests
{
    [Fact]
    public void NativeDocumentationFollowsEachOverloadAndItsSelectedParameter()
    {
        var answer = JsonNode.Parse("""
            {"activeSignature":1,"activeParameter":0,"signatures":[
              {"label":"f(int first, int second)","activeParameter":1,
               "documentation":{"kind":"markdown","value":"Adds **numbers**."},
               "parameters":[{"label":"int first","documentation":"First number."},
                             {"label":"int second","documentation":{"kind":"markdown","value":"The `second` number."}}]},
              {"label":"f(string first)","documentation":"Reads text.",
               "parameters":[{"label":[2,14],"documentation":"Text to read."}]}]}
            """);
        var result = LanguageServer.SignatureOf(answer)!.Value;
        Assert.Equal("Reads text.", result.Documentation);
        Assert.Equal("Text to read.", result.ParameterDocumentation);
        var other = result.Overloads![0];
        Assert.Equal("Adds numbers.", other.Documentation);
        Assert.Equal("The second number.", other.ParameterDocumentation);
        Assert.Equal("int second", other.Text.Substring(other.Start, other.Length));
    }

    [Fact]
    public void DocumentationWithoutAValidActiveParameterDoesNotUseAnotherParametersHelp()
    {
        var answer = JsonNode.Parse("""
            {"activeParameter":3,"signatures":[{"label":"f(int value)","documentation":"Method summary.",
              "parameters":[{"label":"int value","documentation":"Not selected."}]}]}
            """);
        var result = LanguageServer.SignatureOf(answer)!.Value;
        Assert.Equal("Method summary.", result.Documentation);
        Assert.Empty(result.ParameterDocumentation);
        Assert.Equal(0, result.Length);
    }

    [Theory]
    [InlineData(null, null, 1, 7)]
    [InlineData(0, null, 1, 2)]
    [InlineData(1, 0, 1, 2)]
    [InlineData(0, 1, 0, 7)]
    public void EditorArgumentIsUsedOnlyWhenTheServerOmitsTheActiveParameter(int? active, int? perSignature, int fallback, int expected)
    {
        var signature = new JsonObject { ["label"] = "f(int, int)", ["activeParameter"] = perSignature,
            ["parameters"] = new JsonArray(new JsonObject { ["label"] = "int" }, new JsonObject { ["label"] = "int" }) };
        var result = LanguageServer.SignatureOf(new JsonObject { ["activeParameter"] = active, ["signatures"] = new JsonArray(signature) }, fallback);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Start);
    }

    [Theory]
    [InlineData("f(int, int)", "int", 7)]
    [InlineData("f(pair<int, int>, pair<int, int>)", "pair<int, int>", 18)]
    public void RepeatedStringLabelsHighlightTheSelectedOccurrence(string label, string parameter, int expected)
    {
        var signature = new JsonObject { ["label"] = label, ["parameters"] = new JsonArray(
            new JsonObject { ["label"] = parameter }, new JsonObject { ["label"] = parameter }) };
        var result = LanguageServer.SignatureOf(new JsonObject { ["activeParameter"] = 1, ["signatures"] = new JsonArray(signature) });
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Start);
        Assert.Equal(parameter, result.Value.Text.Substring(result.Value.Start, result.Value.Length));
    }

    [Fact]
    public void StringLabelAfterAnOffsetLabelUsesTheLaterOccurrence()
    {
        var result = LanguageServer.SignatureOf(JsonNode.Parse("""
            { "activeParameter":0, "signatures":[{"label":"f(int, int)", "activeParameter":1,
              "parameters":[{"label":[2,5]},{"label":"int"}]}] }
            """));
        Assert.NotNull(result);
        Assert.Equal(7, result.Value.Start);
        Assert.Equal(3, result.Value.Length);
    }
}
