using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public partial class LanguageServerTests
{
    [Fact]
    public async Task NativeCompletionTriggersAndRequestContextsArePreserved()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using var server = LanguageServer.Over("triggers", "cpp", Path.GetTempPath(), toClientRead, toServer);
        Assert.False(server.TriggersCompletion(':'));
        var initialize = ReadMessage(toServerRead);
        Write(toClient, new JsonObject { ["id"] = initialize["id"]!.DeepClone(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject
        {
            ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".", ":", "\"", 42, "too long") }
        } } }.ToJsonString());
        ReadMessage(toServerRead);
        Assert.True(server.TriggersCompletion(':'));
        Assert.True(server.TriggersCompletion('"'));
        Assert.True(server.TriggersCompletion('.'));
        Assert.False(server.TriggersCompletion('>'));
        Assert.False(server.TriggersCompletion('t'));
        foreach (var (trigger, again, kind) in new (char?, bool, int)[] { (':', false, 2), (null, false, 1), (':', true, 3) })
        {
            Task<LanguageServerCompletions?> completing = server.Complete(Path.Combine(Path.GetTempPath(), "trigger.cpp"), 0, 3, 3, trigger, again);
            var request = ReadMessage(toServerRead);
            Assert.Equal("textDocument/completion", request["method"]!.GetValue<string>());
            var context = request["params"]!["context"]!;
            Assert.Equal(kind, context["triggerKind"]!.GetValue<int>());
            Assert.Equal(kind == 2 ? ":" : null, context["triggerCharacter"]?.GetValue<string>());
            Write(toClient, new JsonObject { ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonArray() }.ToJsonString());
            Assert.Empty((await completing.WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Items);
        }
        toClient.Dispose();
        Assert.True(await Task.Run(() => WaitFor(() => server.Failure is not null)));
    }
}
