using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public partial class LanguageServerTests
{
    [Fact]
    public async Task OnTypeFormattingUsesOnlyAdvertisedTriggersAndCurrentUtf16Position()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var fromServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientIn = new AnonymousPipeClientStream(PipeDirection.In, fromServer.ClientSafePipeHandle);
        using var server = LanguageServer.Over("typing", "csharp", Path.GetTempPath(), clientIn, toServer);
        var initialize = ReadMessage(serverIn);
        Assert.False(initialize["params"]!["capabilities"]!["textDocument"]!["onTypeFormatting"]!["dynamicRegistration"]!.GetValue<bool>());
        Write(fromServer, new JsonObject { ["id"] = initialize["id"]!.DeepClone(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject
        {
            ["documentOnTypeFormattingProvider"] = new JsonObject { ["firstTriggerCharacter"] = ";", ["moreTriggerCharacter"] = new JsonArray("}", "\n") }
        } } }.ToJsonString());
        ReadMessage(serverIn);
        Assert.True(server.TriggersFormatting(';'));
        Assert.True(server.TriggersFormatting('\n'));
        Assert.True(server.TriggersFormatting('}'));
        Assert.False(server.TriggersFormatting(':'));
        Assert.Null(await server.FormatOnType("main.cs", 2, 9, ':', 2, true));
        var formatting = server.FormatOnType("main.cs", 2, 9, ';', 2, false);
        var request = ReadMessage(serverIn);
        Assert.Equal("textDocument/onTypeFormatting", request["method"]!.GetValue<string>());
        Assert.Equal(2, request["params"]!["position"]!["line"]!.GetValue<int>());
        Assert.Equal(9, request["params"]!["position"]!["character"]!.GetValue<int>());
        Assert.Equal(";", request["params"]!["ch"]!.GetValue<string>());
        Assert.Equal(2, request["params"]!["options"]!["tabSize"]!.GetValue<int>());
        Assert.False(request["params"]!["options"]!["insertSpaces"]!.GetValue<bool>());
        Write(fromServer, new JsonObject { ["id"] = request["id"]!.DeepClone(), ["result"] = JsonNode.Parse("""
            [{"range":{"start":{"line":2,"character":4},"end":{"line":2,"character":5}},"newText":" = "}]
            """) }.ToJsonString());
        Assert.Equal(new LanguageServerEdit(2, 4, 2, 5, " = "), Assert.Single((await formatting)!));
        fromServer.Dispose();
        Assert.True(await Task.Run(() => WaitFor(() => server.Failure is not null)));
    }
}
