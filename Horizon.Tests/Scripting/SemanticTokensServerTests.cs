using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

/// <summary>
/// Semantic tokens asked of a server in this process over pipes, the test playing the server. It says it does them with
/// a legend of its own, answers the request with the numbers, and asks for them to be asked for again.
/// </summary>
public class SemanticTokensServerTests
{
    [Fact]
    public void A_server_with_a_legend_answers_and_its_numbers_come_back_as_names()
    {
        var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using LanguageServer server = LanguageServer.Over("roslyn", "csharp", Path.GetTempPath(), toClientRead, toServer);

        // The client says it can take them, both ways of asking and being asked to ask again
        JsonObject initialize = ReadMessage(toServerRead);
        JsonNode capabilities = initialize["params"]!["capabilities"]!;
        JsonNode semantic = capabilities["textDocument"]!["semanticTokens"]!;
        Assert.NotNull(semantic["requests"]!["full"]);
        Assert.True(semantic["requests"]!["range"]!.GetValue<bool>());
        Assert.Contains("relative", semantic["formats"]!.AsArray().Select(format => format!.GetValue<string>()));
        Assert.Contains("extensionMethod", semantic["tokenTypes"]!.AsArray().Select(type => type!.GetValue<string>()));
        Assert.True(capabilities["workspace"]!["semanticTokens"]!["refreshSupport"]!.GetValue<bool>());

        var legend = new JsonObject
        {
            ["tokenTypes"] = new JsonArray("class name", "method name", "local name", "field name", "static symbol", "keyword"),
            ["tokenModifiers"] = new JsonArray("declaration", "static", "deprecated")
        };
        Write(toClient, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = initialize["id"]!.GetValue<int>(),
            ["result"] = new JsonObject { ["capabilities"] = new JsonObject { ["semanticTokensProvider"] = new JsonObject { ["legend"] = legend, ["full"] = true, ["range"] = false } } }
        }.ToJsonString());
        Assert.Equal("initialized", ReadMessage(toServerRead)["method"]!.GetValue<string>());
        Assert.True(WaitFor(() => server.Offers("semanticTokensProvider")));

        // class Thing { int count; }
        // var x = Thing.Make();
        string path = Path.Combine(Path.GetTempPath(), "thing.cs");
        Task<IReadOnlyList<SemanticSpan>?> asked = server.SemanticTokens(path, 2);
        JsonObject request = ReadMessage(toServerRead);
        Assert.Equal("textDocument/semanticTokens/full", request["method"]!.GetValue<string>());
        Assert.EndsWith("thing.cs", request["params"]!["textDocument"]!["uri"]!.GetValue<string>());

        Write(toClient, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.GetValue<int>(),
            ["result"] = new JsonObject { ["data"] = new JsonArray(0, 0, 5, 5, 0, 0, 6, 5, 0, 1, 0, 12, 5, 3, 0, 1, 4, 1, 2, 1, 0, 4, 5, 0, 0, 0, 6, 4, 1, 2, 0, 0, 4, 4, 0) }
        }.ToJsonString());

        Assert.True(asked.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(
        [
            new SemanticSpan(0, 6, 5, SyntaxKind.Class, SyntaxModifiers.Declaration),
            new SemanticSpan(0, 18, 5, SyntaxKind.Field),
            new SemanticSpan(1, 4, 1, SyntaxKind.Local, SyntaxModifiers.Declaration),
            new SemanticSpan(1, 8, 5, SyntaxKind.Class),
            new SemanticSpan(1, 14, 4, SyntaxKind.Method, SyntaxModifiers.Static)
        ], asked.Result);

        // The project loaded, the server says what it said is out of date
        Assert.Equal(0, server.SemanticRefreshes);
        Write(toClient, """{"jsonrpc":"2.0","id":"r1","method":"workspace/semanticTokens/refresh","params":null}""");
        JsonObject answer = ReadMessage(toServerRead);
        Assert.Equal("r1", answer["id"]!.GetValue<string>());
        Assert.Equal(1, server.SemanticRefreshes);
    }

    [Fact]
    public void A_server_that_does_not_do_them_answers_null_without_being_asked()
    {
        var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using LanguageServer server = LanguageServer.Over("plain", "text", Path.GetTempPath(), toClientRead, toServer);

        JsonObject initialize = ReadMessage(toServerRead);
        Write(toClient, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = initialize["id"]!.GetValue<int>(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject() } }.ToJsonString());
        Assert.Equal("initialized", ReadMessage(toServerRead)["method"]!.GetValue<string>());
        Assert.True(WaitFor(() => server.IsReady));

        Task<IReadOnlyList<SemanticSpan>?> asked = server.SemanticTokens(Path.Combine(Path.GetTempPath(), "plain.txt"), 1);
        Assert.True(asked.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(asked.Result);
    }

    private static bool WaitFor(Func<bool> until, int milliseconds = 10000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            if (until())
                return true;
            Thread.Sleep(10);
        }

        return until();
    }

    // Helper method to read one message the way a server reads it
    private static JsonObject ReadMessage(Stream from)
    {
        string header = ReadLine(from);
        ReadLine(from);
        byte[] body = new byte[int.Parse(header[16..])];
        from.ReadExactly(body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static string ReadLine(Stream stream)
    {
        var line = new StringBuilder();
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n')
            line.Append((char)b);
        return line.ToString().TrimEnd('\r');
    }

    private static void Write(Stream stream, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        stream.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        stream.Write(body);
        stream.Flush();
    }
}
