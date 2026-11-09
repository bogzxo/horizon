using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class DiagnosticLocationTests
{
    [Fact]
    public void RelatedLocationsKeepMessagesAndDeduplicateValidFileLocations()
    {
        string file = Path.Combine(Path.GetTempPath(), "previous declaration.h");
        JsonObject Entry(string uri, int line) => new() { ["message"] = "Previous declaration", ["location"] = new JsonObject
        {
            ["uri"] = uri, ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = line, ["character"] = 7 } }
        } };
        var entries = new JsonArray(Entry(new Uri(file).AbsoluteUri, 3), Entry(new Uri(file).AbsoluteUri, 3),
            Entry("https://example.com/source", 3), Entry(new Uri(file).AbsoluteUri, -1), new JsonObject(), "invalid");
        var location = Assert.Single(LanguageServer.DiagnosticLocations(entries)!);
        Assert.Equal(file, location.File);
        Assert.Equal(3, location.Line);
        Assert.Equal(7, location.Column);
        Assert.Equal("Previous declaration", location.Message);
    }

    [Fact]
    public void MissingRelatedLocationsKeepExistingDiagnosticEquality()
    {
        Assert.Null(LanguageServer.DiagnosticLocations(null));
        Assert.Null(LanguageServer.DiagnosticLocations(new JsonArray()));
        Assert.Null(LanguageServer.DiagnosticLocations(new JsonObject()));
    }
}
