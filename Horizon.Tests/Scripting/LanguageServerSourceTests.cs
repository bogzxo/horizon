using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public partial class LanguageServerTests
{
    [Fact]
    public async Task SourceViewsUseTheirOriginalUriAndReleaseItOnClose()
    {
        using var to = new AnonymousPipeServerStream(PipeDirection.Out);
        using var input = new AnonymousPipeClientStream(PipeDirection.In, to.ClientSafePipeHandle);
        using var from = new AnonymousPipeServerStream(PipeDirection.Out);
        using var output = new AnonymousPipeClientStream(PipeDirection.In, from.ClientSafePipeHandle);
        using var server = LanguageServer.Over("csharp", "csharp", Path.GetTempPath(), output, to);
        var initialize = ReadMessage(input);
        Assert.True(initialize["params"]!["capabilities"]!["experimental"]!["csharp"]!["metadataUris"]!.GetValue<bool>());
        Write(from, new JsonObject { ["id"] = initialize["id"]!.DeepClone(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject
        {
            ["definitionProvider"] = true, ["hoverProvider"] = true
        } } }.ToJsonString());
        ReadMessage(input);
        string file = Path.Combine(Path.GetTempPath(), "source view.cs"), uri = "csharp:/project folder/Probe.csproj/decompiled/System.Version.cs";
        server.RegisterSource(file, uri);
        var defining = server.Definition(file, 10, 7);
        var request = ReadMessage(input);
        Assert.Equal(new Uri(uri).AbsoluteUri, request["params"]!["textDocument"]!["uri"]!.GetValue<string>());
        Write(from, new JsonObject { ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonObject
        {
            ["uri"] = "csharp:/project/Probe.csproj/decompiled/System.ICloneable.cs",
            ["range"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 2, ["character"] = 5 }, ["end"] = new JsonObject { ["line"] = 2, ["character"] = 15 } }
        } }.ToJsonString());
        Assert.False(Assert.Single(await defining).OnDisk);
        var source = server.Source(uri);
        var metadata = ReadMessage(input);
        Assert.Equal("csharp/metadata", metadata["method"]!.GetValue<string>());
        Assert.Equal(new Uri(uri).AbsoluteUri, metadata["params"]!["textDocument"]!["uri"]!.GetValue<string>());
        Write(from, new JsonObject { ["id"] = metadata["id"]!.DeepClone(), ["result"] = new JsonObject { ["source"] = "class Version {}" } }.ToJsonString());
        Assert.Equal("class Version {}", await source);
        server.ForgetSource(file);
        var hovering = server.Hover(file, 0, 0);
        var hover = ReadMessage(input);
        Assert.Equal(new Uri(file).AbsoluteUri, hover["params"]!["textDocument"]!["uri"]!.GetValue<string>());
        Write(from, new JsonObject { ["id"] = hover["id"]!.DeepClone(), ["result"] = null }.ToJsonString());
        await hovering;
        Assert.Throws<ArgumentException>(() => server.RegisterSource(file, "https://example.com/source.cs"));
        from.Dispose();
        Assert.True(await Task.Run(() => WaitFor(() => server.Failure is not null)));
    }
}
