using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

/// <summary>
/// The language server client, over pipes in this process and over a little server written in Python that says
/// every TODO in a file is a warning, which is what a real one does with a real language.
/// </summary>
public class LanguageServerTests
{
    /// <summary>
    /// A language server of forty lines. Answers initialize, and on didOpen and didChange publishes a warning for every
    /// TODO in the text, an error for every FIXME.
    /// </summary>
    public const string FAKE_SERVER = """
        import sys, json, re

        def read():
            length = 0
            while True:
                line = sys.stdin.buffer.readline()
                if not line:
                    return None
                if line in (b"\r\n", b"\n"):
                    break
                if line.lower().startswith(b"content-length:"):
                    length = int(line.split(b":")[1].strip())
            return json.loads(sys.stdin.buffer.read(length))

        def send(message):
            body = json.dumps(message).encode("utf-8")
            sys.stdout.buffer.write(b"Content-Length: " + str(len(body)).encode() + b"\r\n\r\n" + body)
            sys.stdout.buffer.flush()

        def check(uri, text):
            found = []
            for number, line in enumerate(text.split("\n")):
                for match in re.finditer(r"TODO|FIXME", line):
                    found.append({"range": {"start": {"line": number, "character": match.start()}, "end": {"line": number, "character": match.end()}},
                                  "severity": 1 if match.group() == "FIXME" else 2, "message": match.group() + " left in", "source": "fake"})
            send({"jsonrpc": "2.0", "method": "textDocument/publishDiagnostics", "params": {"uri": uri, "diagnostics": found}})

        while True:
            message = read()
            if message is None:
                break
            method = message.get("method")
            if method == "initialize":
                send({"jsonrpc": "2.0", "id": message["id"], "result": {"capabilities": {"textDocumentSync": 1}}})
                send({"jsonrpc": "2.0", "id": 99, "method": "client/registerCapability", "params": {"registrations": []}})
            elif method == "shutdown":
                send({"jsonrpc": "2.0", "id": message["id"], "result": None})
            elif method == "exit":
                break
            elif method == "textDocument/didOpen":
                check(message["params"]["textDocument"]["uri"], message["params"]["textDocument"]["text"])
            elif method == "textDocument/didChange":
                check(message["params"]["textDocument"]["uri"], message["params"]["contentChanges"][0]["text"])
            elif method == "textDocument/completion":
                line = message["params"]["position"]["line"]
                dotted = message["params"].get("context", {}).get("triggerCharacter") == "."
                items = [{"label": "zeta", "sortText": "3"},
                         {"label": "alpha", "sortText": "1", "detail": "the first", "kind": 3},
                         {"label": "call", "sortText": "2", "kind": 2, "insertText": "call(${1:x}, $0)", "insertTextFormat": 2},
                         {"label": "edited", "sortText": "4", "textEdit": {"range": {"start": {"line": line, "character": 0}, "end": {"line": line, "character": 0}}, "newText": "edited!"}}]
                if dotted:
                    items = [{"label": "member", "kind": 5}]
                send({"jsonrpc": "2.0", "id": message["id"], "result": {"isIncomplete": dotted, "items": items}})
        """;

    private static bool HasPython => LanguageServers.IsAvailable("python");

    private static string WriteFakeServer()
    {
        string path = Path.Combine(Path.GetTempPath(), "horizon-fake-language-server.py");
        File.WriteAllText(path, FAKE_SERVER);
        return path;
    }

    private static bool WaitFor(Func<bool> until, int milliseconds = 10000)
    {
        var started = DateTime.UtcNow;
        while (!until())
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > milliseconds)
                return false;
            Thread.Sleep(10);
        }

        return true;
    }

    [Fact]
    public async Task Messages_are_framed_with_a_content_length_and_answered_by_their_id()
    {
        // This side writes to "toServer" and reads from "toClient", the pretend server the other way round
        var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);

        using var connection = new JsonRpcConnection(toClientRead, toServer);
        JsonNode? told = null;
        connection.OnNotification("hello", parameters => told = parameters);

        Task<JsonNode?> asked = connection.Request("add", new JsonObject { ["a"] = 1, ["b"] = 2 });

        // What went out, read the way a server reads it
        string header = ReadLine(toServerRead);
        Assert.StartsWith("Content-Length: ", header);
        Assert.Equal(string.Empty, ReadLine(toServerRead));
        byte[] body = new byte[int.Parse(header[16..])];
        toServerRead.ReadExactly(body);
        var request = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("add", request["method"]!.GetValue<string>());
        Assert.Equal(1, request["id"]!.GetValue<int>());

        // The answer, and a notification before it
        Write(toClient, """{"jsonrpc":"2.0","method":"hello","params":{"who":"you"}}""");
        Write(toClient, """{"jsonrpc":"2.0","id":1,"result":3}""");

        JsonNode? answered = await asked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, answered!.GetValue<int>());
        Assert.True(WaitFor(() => told is not null));
        Assert.Equal("you", told!["who"]!.GetValue<string>());

        // An error comes back as an exception, and a request from their side is answered without anybody doing anything
        Task<JsonNode?> refused = connection.Request("nothing", null);
        string secondHeader = ReadLine(toServerRead);
        ReadLine(toServerRead);
        toServerRead.ReadExactly(new byte[int.Parse(secondHeader[16..])]);
        Write(toClient, """{"jsonrpc":"2.0","id":2,"error":{"code":-32601,"message":"no such method"}}""");
        var thrown = Assert.Throws<AggregateException>(() => refused.Wait(5000));
        Assert.Contains("no such method", thrown.InnerException!.Message);

        Write(toClient, """{"jsonrpc":"2.0","id":"r1","method":"window/workDoneProgress/create","params":{}}""");
        string answerHeader = ReadLine(toServerRead);
        ReadLine(toServerRead);
        byte[] answer = new byte[int.Parse(answerHeader[16..])];
        toServerRead.ReadExactly(answer);
        Assert.Equal("r1", JsonNode.Parse(answer)!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_message_that_cant_be_made_sense_of_is_lost_and_the_connection_goes_on()
    {
        var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);

        using var connection = new JsonRpcConnection(toClientRead, toServer);
        int heard = 0;
        connection.OnNotification("bad", parameters => throw new InvalidOperationException("can't read that"));
        connection.OnNotification("good", parameters => heard++);

        // A column past an int's room, what serve-d says for the end of a line, read wrong once took the reader down
        Write(toClient, """{"jsonrpc":"2.0","method":"bad","params":{"character":4294967295}}""");
        Write(toClient, """{"jsonrpc":"2.0","method":"good","params":{}}""");
        Assert.True(WaitFor(() => heard == 1));
        Assert.False(connection.IsClosed);

        Task<JsonNode?> asked = connection.Request("still", null);
        string header = ReadLine(toServerRead);
        ReadLine(toServerRead);
        toServerRead.ReadExactly(new byte[int.Parse(header[16..])]);
        Write(toClient, """{"jsonrpc":"2.0","id":1,"result":true}""");
        Assert.True((await asked.WaitAsync(TimeSpan.FromSeconds(5)))!.GetValue<bool>());
    }

    [Theory]
    [InlineData("int x", "x", "int")]
    [InlineData("int lengthSquared()", "lengthSquared ()", "int ()")]
    [InlineData("int[] index", "x", "int[] index")]
    [InlineData("x", "x", "x")]
    public void What_a_server_says_of_an_item_goes_without_the_items_own_name(string detail, string label, string shown) =>
        Assert.Equal(shown, LanguageServer.DetailWithout(detail, label));

    [Fact]
    public void A_server_says_what_is_wrong_with_a_file_as_it_is_opened_and_changed()
    {
        if (!HasPython)
            return;

        string script = WriteFakeServer();
        string folder = Path.Combine(Path.GetTempPath(), "horizon-language-server-test");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "notes.fake");

        using LanguageServer? server = LanguageServer.Start("fake", "python", [script], "fake", folder);
        Assert.NotNull(server);

        int woken = 0;
        server.Woken = () => Interlocked.Increment(ref woken);

        // Opened before initialize is answered, it waits its turn
        server.Open(file, "fine\na TODO here\n");

        string? path = null;
        IReadOnlyList<UICodeDiagnostic>? found = null;
        Assert.True(WaitFor(() => server.TryTake(out path, out found)), server.Failure ?? "nothing came back");
        Assert.True(server.IsReady);
        Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(path!), ignoreCase: true);
        Assert.Equal([new UICodeDiagnostic(1, 2, 4, "TODO left in", Warning: true)], found);
        Assert.True(woken >= 1);

        server.Change(file, "FIXME now\n");
        Assert.True(WaitFor(() => server.TryTake(out path, out found)));
        Assert.Equal([new UICodeDiagnostic(0, 0, 5, "FIXME left in")], found);

        server.Change(file, "all clean\n");
        Assert.True(WaitFor(() => server.TryTake(out path, out found)));
        Assert.Empty(found!);

        server.Close(file);
        Assert.Null(server.Failure);
    }

    [Fact]
    public void A_server_is_found_by_the_files_extension_and_started_once()
    {
        if (!HasPython)
            return;

        string script = WriteFakeServer();
        LanguageServers.Register("fake-test", "python", [script], "fake", [".faketest"]);

        Assert.Null(LanguageServers.For("notes.txt", Path.GetTempPath()));
        Assert.Equal("fake-test", LanguageServers.RegistrationFor("a.FAKETEST")?.Name);

        LanguageServer? first = LanguageServers.For("a.faketest", Path.GetTempPath());
        Assert.NotNull(first);
        Assert.Same(first, LanguageServers.For("b.faketest", Path.GetTempPath()));
        Assert.Contains(first, LanguageServers.Running);

        // A command that isn't on this machine is no server, and isn't asked for again
        LanguageServers.Register("missing", "no-such-language-server-anywhere", [], "none", [".missingtest"]);
        Assert.Null(LanguageServers.For("x.missingtest", Path.GetTempPath()));
        Assert.False(LanguageServers.IsAvailable("no-such-language-server-anywhere"));

        LanguageServers.StopAll();
        Assert.Empty(LanguageServers.Running);
    }

    [Fact]
    public async Task A_server_offers_what_can_be_written_at_a_place_in_its_order()
    {
        if (!HasPython)
            return;

        string script = WriteFakeServer();
        string folder = Path.Combine(Path.GetTempPath(), "horizon-language-server-test");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "complete.fake");

        using LanguageServer? server = LanguageServer.Start("fake", "python", [script], "fake", folder);
        Assert.NotNull(server);

        // Asked before initialize is answered, behind the file being opened
        server.Open(file, "a\n");
        LanguageServerCompletions? offered = await server.Complete(file, 0, 1, start: 0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(offered);
        Assert.False(offered.Value.Incomplete);
        Assert.Equal(
        [
            new UICompletion("alpha", "the first"),
            new UICompletion("call", "method", "call(x, )"),
            new UICompletion("zeta"),
            new UICompletion("edited", Insert: "edited!")
        ], offered.Value.Items);

        // An edit that doesn't replace from where the editor will isn't taken, the label goes in instead
        offered = await server.Complete(file, 0, 1, start: 1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new UICompletion("edited"), offered!.Value.Items[^1]);

        // After a dot, the server is told it was a dot
        offered = await server.Complete(file, 0, 2, start: 2, trigger: '.').WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(offered!.Value.Incomplete);
        Assert.Equal([new UICompletion("member", "field")], offered.Value.Items);
    }

    [Fact]
    public void A_question_the_server_asks_waits_for_its_answer_and_the_rest_goes_on_meanwhile()
    {
        var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        using LanguageServer server = LanguageServer.Over("asker", "d", Path.GetTempPath(), toClientRead, toServer);

        // initialize answered, initialized heard
        JsonObject initialize = ReadMessage(toServerRead);
        Write(toClient, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = initialize["id"]!.GetValue<int>(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject() } }.ToJsonString());
        Assert.Equal("initialized", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        // serve-d's question, and a line said while it waits
        Write(toClient, """{"jsonrpc":"2.0","id":"q1","method":"window/showMessageRequest","params":{"type":1,"message":" DCD is outdated.","actions":[{"title":" Download DCD"}]}}""");
        Write(toClient, """{"jsonrpc":"2.0","method":"coded/logInstall","params":"Downloading DCD"}""");

        LanguageServerQuestion? question = null;
        Assert.True(WaitFor(() => server.TryTakeQuestion(out question)));
        Assert.Equal("DCD is outdated.", question!.Message);
        Assert.Equal([" Download DCD"], question.Actions);
        Assert.True(question.Warning);
        Assert.True(WaitFor(() => server.TryTakeSaid(out string text, out _) && text == "Downloading DCD"));

        // Answered, the action itself goes back under the question's id
        question.Answer(" Download DCD");
        JsonObject answer = ReadMessage(toServerRead);
        Assert.Equal("q1", answer["id"]!.GetValue<string>());
        Assert.Equal(" Download DCD", answer["result"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void A_server_with_settings_is_told_them_as_it_starts_and_asked_for_one_it_gets_that_section()
    {
        var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        var settings = new JsonObject { ["d"] = new JsonObject { ["lintOnFileOpen"] = "always" } };
        using LanguageServer server = LanguageServer.Over("told", "d", Path.GetTempPath(), toClientRead, toServer, settings);

        JsonObject initialize = ReadMessage(toServerRead);
        Assert.True(initialize["params"]!["capabilities"]!["workspace"]!["configuration"]!.GetValue<bool>());
        Assert.True(initialize["params"]!["capabilities"]!["textDocument"]!["synchronization"]!["didSave"]!.GetValue<bool>());
        Write(toClient, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = initialize["id"]!.GetValue<int>(), ["result"] = new JsonObject { ["capabilities"] = new JsonObject() } }.ToJsonString());
        Assert.Equal("initialized", ReadMessage(toServerRead)["method"]!.GetValue<string>());

        JsonObject told = ReadMessage(toServerRead);
        Assert.Equal("workspace/didChangeConfiguration", told["method"]!.GetValue<string>());
        Assert.Equal("always", told["params"]!["settings"]!["d"]!["lintOnFileOpen"]!.GetValue<string>());

        // Asked for a section, and one it hasn't got
        Write(toClient, """{"jsonrpc":"2.0","id":"c1","method":"workspace/configuration","params":{"items":[{"section":"d"},{"section":"d.lintOnFileOpen"},{"section":"dfmt"}]}}""");
        JsonObject answer = ReadMessage(toServerRead);
        Assert.Equal("c1", answer["id"]!.GetValue<string>());
        var sections = answer["result"]!.AsArray();
        Assert.Equal("always", sections[0]!["lintOnFileOpen"]!.GetValue<string>());
        Assert.Equal("always", sections[1]!.GetValue<string>());
        Assert.Null(sections[2]);

        // A file saved is said to be
        server.Saved(Path.Combine(Path.GetTempPath(), "saved.d"));
        JsonObject saved = ReadMessage(toServerRead);
        Assert.Equal("textDocument/didSave", saved["method"]!.GetValue<string>());
        Assert.EndsWith("saved.d", saved["params"]!["textDocument"]!["uri"]!.GetValue<string>());
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

    [Fact]
    public async Task What_is_asked_of_a_server_that_stopped_is_answered_with_nothing()
    {
        var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);

        using LanguageServer server = LanguageServer.Over("gone", "fake", Path.GetTempPath(), toClientRead, toServer);
        Task<LanguageServerCompletions?> asked = server.Complete("a.fake", 0, 0, 0);

        // The other side goes before initialize is answered
        toClient.Dispose();
        Assert.Null(await asked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(server.Failure);
        Assert.Null(await server.Complete("a.fake", 0, 0, 0).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("call($1)$0", "call()")]
    [InlineData("for (${1:i} = 0; $1 < ${2:count}; $1++)", "for (i = 0;  < count; ++)")]
    [InlineData("${1|public,private|} void", "public void")]
    [InlineData("${1:outer ${2:inner}}", "outer inner")]
    [InlineData("cost \\$5 $", "cost $5 $")]
    public void A_snippet_is_written_without_its_placeholders(string snippet, string plain) =>
        Assert.Equal(plain, LanguageServer.PlainSnippet(snippet));

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
