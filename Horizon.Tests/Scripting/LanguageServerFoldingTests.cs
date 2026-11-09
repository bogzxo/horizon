using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public partial class LanguageServerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFoldingAndPythonSymbolFallbackPreserveValidWholeLineRanges(bool python)
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using var server = LanguageServer.Over("folding", python ? "python" : "csharp", Path.GetTempPath(), toClientRead, toServer);
        var initialize = ReadMessage(toServerRead);
        Assert.True(initialize["params"]!["capabilities"]!["textDocument"]!["foldingRange"]!["lineFoldingOnly"]!.GetValue<bool>());
        Write(toClient, new JsonObject { ["id"] = initialize["id"]!.DeepClone(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject
        {
            ["foldingRangeProvider"] = !python, ["documentSymbolProvider"] = true
        } } }.ToJsonString());
        ReadMessage(toServerRead);
        string file = Path.Combine(Path.GetTempPath(), "folding.fake");
        var folding = server.FoldingRanges(file, 8);
        var request = ReadMessage(toServerRead);
        Assert.Equal(python ? "textDocument/documentSymbol" : "textDocument/foldingRange", request["method"]!.GetValue<string>());
        string result = python ? """
            [{"name":"Box","kind":5,"range":{"start":{"line":0,"character":0},"end":{"line":6,"character":0}},
              "selectionRange":{"start":{"line":0,"character":6},"end":{"line":0,"character":9}},
              "children":[{"name":"method","kind":6,"range":{"start":{"line":2,"character":0},"end":{"line":4,"character":8}}}]},
             {"name":"outside","kind":12,"location":{"uri":"file:///unrelated.py","range":{"start":{"line":0,"character":0},"end":{"line":3,"character":1}}}}]
            """ : """
            [{"startLine":0,"endLine":5,"kind":"region"},{"startLine":2,"endLine":4,"kind":"comment"},
             {"startLine":-1,"endLine":2},{"startLine":2,"endLine":2},{"startLine":0,"endLine":8}]
            """;
        Write(toClient, new JsonObject { ["id"] = request["id"]!.DeepClone(), ["result"] = JsonNode.Parse(result) }.ToJsonString());
        var ranges = await folding.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(ranges);
        Assert.Equal(new[] { (0, 5), (2, 4) }, ranges.Select(range => (range.Start, range.End)));
        if (!python) Assert.Equal("region", ranges[0].Kind);
        toClient.Dispose();
        Assert.True(await Task.Run(() => WaitFor(() => server.Failure is not null)));
    }
}
