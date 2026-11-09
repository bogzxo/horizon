using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public sealed class LanguageServerEditRequestTests
{
    [Theory]
    [InlineData(2, 2, 2, true)]
    [InlineData(1, 2, 2, false)]
    [InlineData(2, 2, 3, false)]
    [InlineData(null, 2, 2, true)]
    [InlineData(null, 2, 3, false)]
    [InlineData(2, 2, null, false)]
    public async Task ResponseWaitsForApplicationAndRejectsStaleVersions(int? requested, int received, int? current, bool valid)
    {
        var response = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new LanguageServerEditRequest(
            new Dictionary<string, IReadOnlyList<LanguageServerEdit>> { ["file"] = [new(0, 0, 0, 1, "x")] },
            new Dictionary<string, int?> { ["file"] = requested },
            new Dictionary<string, int> { ["file"] = received }, response);
        Assert.False(response.Task.IsCompleted);
        Assert.Equal(valid, request.VersionsMatch(_ => current));
        request.Complete(valid, valid ? null : "stale");
        var answer = await response.Task;
        Assert.Equal(valid, answer!["applied"]!.GetValue<bool>());
        if (!valid) Assert.Equal("stale", answer["failureReason"]!.GetValue<string>());
        request.Complete(!valid);
        Assert.Equal(valid, (await response.Task)!["applied"]!.GetValue<bool>());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyEditRpcWaitsForEditorAndReturnsItsActualResult(bool applied)
    {
        Assert.True(LanguageServers.IsAvailable("python"), "Python is required for the real protocol fixture.");
        string folder = Directory.CreateTempSubdirectory("horizon-edit-rpc-").FullName;
        string script = Path.Combine(folder, "server.py"), ack = Path.Combine(folder, "ack.json");
        string source = LanguageServerTests.FAKE_SERVER.ReplaceLineEndings("\n");
        source = source[..source.IndexOf("\nwhile True:\n    message", StringComparison.Ordinal)];
        source += "\n" + """
            pending = None
            ack = None
            while True:
                message = read()
                if message is None: break
                method = message.get("method")
                if method == "initialize":
                    send({"jsonrpc":"2.0", "id":message["id"], "result":{"capabilities":{"textDocumentSync":1}}})
                elif method == "workspace/executeCommand":
                    pending = message["id"]
                    uri, ack = message["params"]["arguments"]
                    send({"jsonrpc":"2.0", "id":901, "method":"workspace/applyEdit", "params":{"edit":{"documentChanges":[{"textDocument":{"uri":uri,"version":1},"edits":[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"y"}]}]}}})
                elif message.get("id") == 901 and "result" in message:
                    with open(ack, "w") as f: json.dump(message["result"], f)
                    send({"jsonrpc":"2.0", "id":pending, "result":None})
                elif method == "shutdown":
                    send({"jsonrpc":"2.0", "id":message["id"], "result":None})
                elif method == "exit": break
            """;
        File.WriteAllText(script, source);
        try
        {
            using var server = LanguageServer.Start("edit-rpc", "python", [script], "fake", folder);
            Assert.NotNull(server);
            string file = Path.Combine(folder, "source.fake");
            server.Open(file, "x");
            Task executing = server.Execute(new JsonObject { ["command"] = "fix", ["arguments"] = new JsonArray(new Uri(file).AbsoluteUri, ack) });
            LanguageServerEditRequest? request = null;
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!server.TryTakeEdits(out request) && DateTime.UtcNow < until)
                await Task.Delay(10);
            Assert.NotNull(request);
            Assert.False(executing.IsCompleted);
            Assert.False(File.Exists(ack));
            Assert.True(request.VersionsMatch(server.DocumentVersion));
            request.Complete(applied, applied ? null : "buffer changed");
            await executing.WaitAsync(TimeSpan.FromSeconds(10));
            var response = JsonNode.Parse(File.ReadAllText(ack));
            Assert.Equal(applied, response!["applied"]!.GetValue<bool>());
            if (!applied) Assert.Equal("buffer changed", response["failureReason"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
