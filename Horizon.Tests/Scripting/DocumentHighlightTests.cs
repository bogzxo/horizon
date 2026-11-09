using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class DocumentHighlightTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeKindsAndMultilineRangesArePreserved(int kind)
    {
        var result = LanguageServer.HighlightsOf(JsonNode.Parse("[{\"range\":{\"start\":{\"line\":1,\"character\":2},\"end\":{\"line\":3,\"character\":4}},\"kind\":" + kind + "}]"));
        Assert.Equal(new LanguageServerHighlight(1, 2, 3, 4, kind), Assert.Single(result));
    }

    [Fact]
    public void MissingKindDefaultsToTextAndInvalidRangesAreIgnored()
    {
        var result = LanguageServer.HighlightsOf(JsonNode.Parse("""
            [
              {"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":4}}},
              {"range":{"start":{"line":0,"character":4},"end":{"line":0,"character":1}}},
              {"range":{"start":{"line":-1,"character":0},"end":{"line":0,"character":1}}}
            ]
            """));
        Assert.Equal(new LanguageServerHighlight(0, 1, 0, 4, 1), Assert.Single(result));
        Assert.Empty(LanguageServer.HighlightsOf(null));
    }
}
