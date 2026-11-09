using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class SelectionRangeTests
{
    [Theory]
    [InlineData(0, 10, 2)]
    [InlineData(2, 4, 1)]
    [InlineData(3, 5, 1)]
    public void ParentsMustContainChildrenAndDuplicatesAreRemoved(int start, int end, int count)
    {
        JsonObject Range(int from, int to) => new() { ["start"] = new JsonObject { ["line"] = 0, ["character"] = from }, ["end"] = new JsonObject { ["line"] = 0, ["character"] = to } };
        var child = new JsonObject { ["range"] = Range(2, 4), ["parent"] = new JsonObject { ["range"] = Range(start, end) } };
        var ranges = LanguageServer.SelectionRangesOf(new JsonArray(child));
        Assert.Equal(count, ranges.Count);
        Assert.Equal(new LanguageServerSelection(new(0, 2), new(0, 4)), ranges[0]);
    }

    [Fact]
    public void NullAndInvalidRangesDoNotSelectText()
    {
        Assert.Empty(LanguageServer.SelectionRangesOf(null));
        Assert.Empty(LanguageServer.SelectionRangesOf(JsonNode.Parse("[{\"range\":{\"start\":{\"line\":0,\"character\":5},\"end\":{\"line\":0,\"character\":2}}}]")));
    }
}
