using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public partial class LanguageServerTests
{
    [Fact]
    public async Task NativeRangesMayStartBeforeWordBoundaryButMustContainCaret()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using var server = LanguageServer.Over("ranges", "cpp", Path.GetTempPath(), toClientRead, toServer);
        var initialize = ReadMessage(toServerRead);
        Write(toClient, new JsonObject { ["id"] = initialize["id"]!.DeepClone(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject() } }.ToJsonString());
        ReadMessage(toServerRead);
        Task<LanguageServerCompletions?> completing = server.Complete(Path.Combine(Path.GetTempPath(), "ranges.cpp"), 0, 25, 23);
        var request = ReadMessage(toServerRead);
        JsonObject Item(string label, int firstLine, int firstColumn, int lastLine, int lastColumn) => new()
        {
            ["label"] = label, ["textEdit"] = new JsonObject
            {
                ["newText"] = "answer-value.h\"", ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = firstLine, ["character"] = firstColumn },
                    ["end"] = new JsonObject { ["line"] = lastLine, ["character"] = lastColumn }
                }
            }
        };
        Write(toClient, new JsonObject { ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonArray(
            Item("path", 0, 17, 0, 26), Item("prefix", 0, 23, 0, 25),
            Item("negative", 0, -1, 0, 25), Item("past-caret", 0, 26, 0, 26),
            Item("before-caret", 0, 17, 0, 24), Item("multiline", 0, 17, 1, 0), Item("other-line", 1, 17, 1, 26)) }.ToJsonString());
        var items = (await completing.WaitAsync(TimeSpan.FromSeconds(5)))!.Value.Items.ToDictionary(item => item.Text);
        Assert.Equal(2, items.Count);
        Assert.Equal(new UITextPosition(0, 17), items["path"].ReplacementStart);
        Assert.Equal(new UITextPosition(0, 26), items["path"].ReplacementEnd);
        Assert.Equal("answer-value.h\"", items["path"].Insert);
        Assert.Null(items["prefix"].ReplacementStart);
        Assert.Null(items["prefix"].ReplacementEnd);
        toClient.Dispose();
        Assert.True(await Task.Run(() => WaitFor(() => server.Failure is not null)));
    }
}
