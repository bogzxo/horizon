using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public partial class LanguageServerTests
{
    [Theory]
    [InlineData("2", "int value = 1;\n", "int value = 42;\n")]
    [InlineData("{\"openClose\":true,\"change\":2}", "first\nsecond\n", "first\nnew\nsecond\n")]
    [InlineData("2", "x😀a", "x😁a")]
    [InlineData("2", "ab\r\ncd", "ab\ncd")]
    [InlineData("2", "ab\rcd", "ab\rXcd")]
    [InlineData("2", "\r", "\r\n")]
    [InlineData("2", "\r\n", "\n")]
    [InlineData("2", "", "😀\n")]
    [InlineData("2", "gone\n", "")]
    [InlineData("2", "same", "same")]
    [InlineData("1", "old", "new")]
    [InlineData("{\"change\":1}", "old", "new")]
    [InlineData("null", "old", "new")]
    public async Task ChangesRespectNegotiatedSyncAndReconstructExactText(string sync, string before, string after)
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using var server = LanguageServer.Over("sync", "fake", Path.GetTempPath(), toClientRead, toServer);
        var initialize = ReadMessage(toServerRead);
        Write(toClient, new JsonObject { ["id"] = initialize["id"]!.DeepClone(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject { ["textDocumentSync"] = JsonNode.Parse(sync) } } }.ToJsonString());
        Assert.Equal("initialized", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        string file = Path.Combine(Path.GetTempPath(), "sync.fake");
        server.Open(file, before);
        int version = ReadMessage(toServerRead)["params"]!["textDocument"]!["version"]!.GetValue<int>();
        foreach (string wanted in new[] { after, before, after })
        {
            server.Change(file, wanted);
            var changed = ReadMessage(toServerRead);
            Assert.Equal("textDocument/didChange", changed["method"]!.GetValue<string>());
            int next = changed["params"]!["textDocument"]!["version"]!.GetValue<int>();
            Assert.True(next > version);
            version = next;
            var change = Assert.Single(changed["params"]!["contentChanges"]!.AsArray())!;
            bool incremental = sync == "2" || sync.Contains("\"change\":2");
            Assert.Equal(incremental, change["range"] is JsonObject);
            before = ApplySynchronizedChange(before, change);
            Assert.Equal(wanted, before);
        }

        // Closing drops the previous snapshot; reopening starts from the new content.
        server.Close(file);
        Assert.Equal("textDocument/didClose", ReadMessage(toServerRead)["method"]!.GetValue<string>());
        server.Open(file, "replacement\n");
        ReadMessage(toServerRead);
        server.Change(file, "replacement!\n");
        var reopened = ReadMessage(toServerRead)["params"]!["contentChanges"]![0]!;
        Assert.Equal("replacement!\n", ApplySynchronizedChange("replacement\n", reopened));
        toClient.Dispose();
        Assert.True(await Task.Run(() => WaitFor(() => !server.IsReady || server.Failure is not null)));
    }

    [Fact]
    public async Task OneCharacterEditInLargeDocumentDoesNotSendTheWholeFile()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using var server = LanguageServer.Over("large-sync", "fake", Path.GetTempPath(), toClientRead, toServer);
        var initialize = ReadMessage(toServerRead);
        Write(toClient, new JsonObject { ["id"] = initialize["id"]!.DeepClone(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject { ["textDocumentSync"] = 2 } } }.ToJsonString());
        ReadMessage(toServerRead);
        string file = Path.Combine(Path.GetTempPath(), "large-sync.fake");
        string text = string.Concat(Enumerable.Repeat("int value = 42;\n", 20000));
        Task opening = Task.Run(() => server.Open(file, text));
        var opened = await Task.Run(() => ReadMessage(toServerRead)).WaitAsync(TimeSpan.FromSeconds(5));
        await opening;
        Assert.Equal(text, opened["params"]!["textDocument"]!["text"]!.GetValue<string>());
        int at = text.Length / 2;
        string edited = text.Insert(at, "X");
        // No pipe reader until this returns: a full-document write would fill and block the pipe.
        await Task.Run(() => server.Change(file, edited)).WaitAsync(TimeSpan.FromSeconds(2));
        var changed = ReadMessage(toServerRead);
        Assert.True(changed.ToJsonString().Length < 512);
        var change = changed["params"]!["contentChanges"]![0]!;
        Assert.Equal("X", change["text"]!.GetValue<string>());
        Assert.Equal(edited, ApplySynchronizedChange(text, change));
        toClient.Dispose();
        Assert.True(await Task.Run(() => WaitFor(() => server.Failure is not null)));
    }

    private static string ApplySynchronizedChange(string before, JsonNode change)
    {
        string replacement = change["text"]!.GetValue<string>();
        if (change["range"] is not JsonObject range) return replacement;
        int Offset(JsonNode position)
        {
            int line = position["line"]!.GetValue<int>(), character = position["character"]!.GetValue<int>();
            int at = 0;
            for (int seen = 0; seen < line; at++)
            {
                Assert.True(at < before.Length);
                if (before[at] == '\r')
                {
                    if (at + 1 < before.Length && before[at + 1] == '\n') at++;
                    seen++;
                }
                else if (before[at] == '\n') seen++;
            }
            return at + character;
        }
        int start = Offset(range["start"]!), end = Offset(range["end"]!);
        Assert.False(start > 0 && start < before.Length && char.IsHighSurrogate(before[start - 1]) && char.IsLowSurrogate(before[start]));
        Assert.False(end > 0 && end < before.Length && char.IsHighSurrogate(before[end - 1]) && char.IsLowSurrogate(before[end]));
        return before[..start] + replacement + before[end..];
    }
}
