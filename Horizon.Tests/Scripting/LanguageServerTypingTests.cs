using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public partial class LanguageServerTests
{
    // csharp-ls answers in turn, and the folds, colours and lenses asked after every change put each completion behind
    // them. While typing goes on they're held, and go out in order once it stops.
    [Fact]
    public async Task BackgroundRequestsWaitForTypingAndCompletionToSettle()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using var server = LanguageServer.Over("typing", "csharp", Path.GetTempPath(), toClientRead, toServer);
        var initialize = ReadMessage(toServerRead);
        Write(toClient, new JsonObject { ["id"] = initialize["id"]!.DeepClone(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject { ["foldingRangeProvider"] = true } } }.ToJsonString());
        ReadMessage(toServerRead);

        string file = Path.Combine(Path.GetTempPath(), "typing.cs");
        server.Open(file, "class A {}");
        Assert.Equal("textDocument/didOpen", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        // Typing, then the folds asked for, then a completion: the completion goes first
        server.Change(file, "class A { }");
        Assert.Equal("textDocument/didChange", ReadMessage(toServerRead)["method"]!.GetValue<string>());
        Task<IReadOnlyList<UICodeFoldingRange>?> folds = server.FoldingRanges(file, 1);
        Task<LanguageServerCompletions?> completing = server.Complete(file, 0, 9, 9);
        var completion = ReadMessage(toServerRead);
        Assert.Equal("textDocument/completion", completion["method"]!.GetValue<string>());
        Write(toClient, new JsonObject { ["id"] = completion["id"]!.DeepClone(), ["result"] = new JsonArray() }.ToJsonString());
        await completing.WaitAsync(TimeSpan.FromSeconds(5));

        // Then the folds, a moment after it all stopped
        var folding = ReadMessage(toServerRead);
        Assert.Equal("textDocument/foldingRange", folding["method"]!.GetValue<string>());
        Write(toClient, new JsonObject { ["id"] = folding["id"]!.DeepClone(), ["result"] = new JsonArray() }.ToJsonString());
        await folds.WaitAsync(TimeSpan.FromSeconds(5));

        // With nothing typed for a while they go straight out
        await Task.Delay(800);
        Task<IReadOnlyList<UICodeFoldingRange>?> later = server.FoldingRanges(file, 1);
        var again = ReadMessage(toServerRead);
        Assert.Equal("textDocument/foldingRange", again["method"]!.GetValue<string>());
        Write(toClient, new JsonObject { ["id"] = again["id"]!.DeepClone(), ["result"] = new JsonArray() }.ToJsonString());
        await later.WaitAsync(TimeSpan.FromSeconds(1));

        // Its reading end can only be let go of on Linux and macOS once the writing end has gone, which the usings below would do after it
        toClient.Dispose();
    }
}
