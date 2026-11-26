using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests.Scripting;

public class Pass5ToolingTests
{
    private sealed class BlockingReadStream : Stream
    {
        private readonly ManualResetEventSlim gate = new(false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            gate.Wait();
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            gate.Set();
            base.Dispose(disposing);
        }
    }

    private sealed class OverlapDetectingWriteStream : Stream
    {
        private int activeWriters;
        private int disposed;
        public int Overlaps;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush()
        {
            if (Volatile.Read(ref disposed) != 0)
                Interlocked.Increment(ref Overlaps);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Increment(ref activeWriters) > 1 || Volatile.Read(ref disposed) != 0)
                Interlocked.Increment(ref Overlaps);
            Thread.SpinWait(50);
            Interlocked.Decrement(ref activeWriters);
        }
        protected override void Dispose(bool disposing)
        {
            if (Volatile.Read(ref activeWriters) > 0)
                Interlocked.Increment(ref Overlaps);
            Interlocked.Exchange(ref disposed, 1);
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task JsonRpcConnection_Dispose_fails_pending_requests_and_fires_Closed_once()
    {
        using var input = new BlockingReadStream();
        using var output = new MemoryStream();
        var connection = new JsonRpcConnection(input, output);

        int closedCount = 0;
        connection.Closed = _ => Interlocked.Increment(ref closedCount);

        Task<JsonNode?> first = connection.Request("workspace/symbol", new JsonObject { ["query"] = "a" });
        Task<JsonNode?> second = connection.Request("textDocument/hover", new JsonObject());

        connection.Dispose();
        connection.Dispose();

        await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(connection.IsClosed);
        Assert.Equal(1, Volatile.Read(ref closedCount));

        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Request("after/dispose", null).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task JsonRpcConnection_concurrent_Send_and_Dispose_never_overlap_output_stream()
    {
        for (int round = 0; round < 10; round++)
        {
            using var input = new BlockingReadStream();
            var output = new OverlapDetectingWriteStream();
            var connection = new JsonRpcConnection(input, output);

            using var start = new ManualResetEventSlim(false);
            Task[] senders = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                start.Wait();
                for (int i = 0; i < 40; i++)
                {
                    try { connection.Notify("ping", new JsonObject { ["i"] = i }); }
                    catch (InvalidOperationException) { break; }
                }
            })).ToArray();

            Task disposer = Task.Run(() =>
            {
                start.Wait();
                Thread.SpinWait(100);
                connection.Dispose();
            });

            start.Set();
            await Task.WhenAll(senders.Append(disposer)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref output.Overlaps));
            Assert.True(connection.IsClosed);
        }
    }

    [Fact]
    public async Task DebugAdapterConnection_Dispose_fails_pending_requests_and_fires_Closed_once()
    {
        using var input = new BlockingReadStream();
        using var output = new MemoryStream();
        var connection = new DebugAdapterConnection(input, output);

        int closedCount = 0;
        connection.Closed = _ => Interlocked.Increment(ref closedCount);

        Task<JsonNode?> pending = connection.Request("threads");
        connection.Dispose();
        connection.Dispose();

        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(connection.IsClosed);
        Assert.Equal(1, Volatile.Read(ref closedCount));
    }

    [Theory]
    [InlineData("cOnTeNt-LeNgTh:\t 0 \r\n\r\n", 0)]
    [InlineData("Content-Length:   42\t\r\n\r\n", 42)]
    [InlineData("Content-Length: -5\r\n\r\n", -1)]
    [InlineData("Content-Length: 12abc\r\n\r\n", -1)]
    [InlineData("Content-Length: 99999999999999999999\r\n\r\n", -1)]
    [InlineData("Content-Length:   \r\n\r\n", -1)]
    [InlineData("Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n", -1)]
    public void ContentLengthReader_parses_and_rejects_header_edge_cases(string raw, int expected)
    {
        var reader = new ContentLengthReader(new MemoryStream(Encoding.ASCII.GetBytes(raw)));
        Assert.Equal(expected, reader.ReadHeaders(overBlankLinesFirst: false));
    }

    [Fact]
    public void ContentLengthReader_handles_zero_length_body_and_leading_blank_lines()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("\r\n\nContent-Length: 0\r\n\r\nContent-Length: 2\r\n\r\n{}");
        var reader = new ContentLengthReader(new MemoryStream(bytes));

        Assert.Equal(0, reader.ReadHeaders(overBlankLinesFirst: true));
        ReadOnlyMemory<byte>? empty = reader.ReadBody(0);
        Assert.NotNull(empty);
        Assert.Empty(empty!.Value.ToArray());

        Assert.Equal(2, reader.ReadHeaders(overBlankLinesFirst: false));
        ReadOnlyMemory<byte>? body = reader.ReadBody(2);
        Assert.NotNull(body);
        Assert.Equal("{}", Encoding.UTF8.GetString(body!.Value.Span));
    }

    [Fact]
    public void LanguageServer_Dispose_clears_pull_timers_and_remote_close_propagates_Failure()
    {
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toServerRead = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var toClientRead = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);

        using var server = LanguageServer.Over("pull", "typescript", Path.GetTempPath(), toClientRead, toServer, pullDiagnostics: true);
        using var serverWriter = toClient;

        JsonObject init = ReadFramed(toServerRead);
        WriteFramed(toClient, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = init["id"]!.GetValue<int>(),
            ["result"] = new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["diagnosticProvider"] = new JsonObject { ["interFileDependencies"] = false, ["workspaceDiagnostics"] = false }
                }
            }
        });
        Assert.Equal("initialized", ReadFramed(toServerRead)["method"]!.GetValue<string>());

        string file = Path.Combine(Path.GetTempPath(), "pull-timer-test.ts");
        server.Open(file, "const x = 1;");
        Assert.Equal("textDocument/didOpen", ReadFramed(toServerRead)["method"]!.GetValue<string>());
        Assert.True(WaitFor(() => server.ActivePullTimerCount == 1));

        // Closing the server end should fail the server and dispose all pull timers cleanly
        toClient.Dispose();
        Assert.True(WaitFor(() => server.Failure is not null));
        Assert.Equal(0, server.ActivePullTimerCount);

        server.Dispose();
        Assert.Equal(0, server.ActivePullTimerCount);
    }

    [Fact]
    public async Task DebugSession_Configure_times_out_cleanly_when_adapter_never_initializes()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);

        using var session = DebugSession.Over(sessionIn, toAdapter);
        session.ConfigureTimeout = TimeSpan.FromMilliseconds(80);

        Task<bool> launching = session.Launch("fixture", [], Path.GetTempPath(), new Dictionary<string, IReadOnlyCollection<int>>());
        JsonObject initialize = ReadFramed(adapterIn);
        WriteFramed(fromAdapter, new JsonObject
        {
            ["seq"] = 1,
            ["type"] = "response",
            ["request_seq"] = initialize["seq"]!.GetValue<int>(),
            ["command"] = "initialize",
            ["success"] = true
        });
        JsonObject launch = ReadFramed(adapterIn);
        Assert.Equal("launch", launch["command"]!.GetValue<string>());

        bool ok = await launching.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(ok);
        Assert.Equal(DebugState.Ended, session.State);
        Assert.NotNull(session.Failed);
        Assert.Contains("Timed out", session.Failed!, StringComparison.OrdinalIgnoreCase);

        fromAdapter.Dispose();
    }

    [Fact]
    public async Task DebugSession_End_does_not_block_on_live_debuggee_and_guards_post_dispose_calls()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);

        using var session = DebugSession.Over(sessionIn, toAdapter);

        // Attach the session's debuggee handle to the current live test host process so it will not exit
        WriteFramed(fromAdapter, new JsonObject
        {
            ["seq"] = 1,
            ["type"] = "event",
            ["event"] = "process",
            ["body"] = new JsonObject { ["systemProcessId"] = Environment.ProcessId }
        });
        Assert.True(WaitFor(() => session.ProcessId == Environment.ProcessId));

        var sw = Stopwatch.StartNew();
        WriteFramed(fromAdapter, new JsonObject
        {
            ["seq"] = 2,
            ["type"] = "event",
            ["event"] = "exited",
            ["body"] = new JsonObject { ["exitCode"] = 7 }
        });
        Assert.True(WaitFor(() => session.State == DebugState.Ended));
        sw.Stop();

        // Previously ExitCodeOf called program.WaitForExit(500), blocking the reader thread for >= 500 ms.
        Assert.True(sw.ElapsedMilliseconds < 400, $"End() took {sw.ElapsedMilliseconds}ms and may still be blocking on WaitForExit.");

        // Second termination event and Dispose must not emit duplicate Ended events
        WriteFramed(fromAdapter, new JsonObject { ["seq"] = 3, ["type"] = "event", ["event"] = "terminated" });
        int endedEvents = 0;
        int exitCode = -1;
        while (session.TryTake(out DebugEvent ev))
        {
            if (ev.Kind == DebugEventKind.Ended)
            {
                endedEvents++;
                exitCode = ev.ExitCode;
            }
        }
        Assert.Equal(1, endedEvents);
        Assert.Equal(7, exitCode);

        // Post-ended calls return cleanly without sending requests or throwing
        Assert.Empty(await session.Scopes(1).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(await session.Variables(1).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(await session.Threads().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(string.Empty, await session.Evaluate("1+1", 1).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null((await session.EvaluateSaying("1+1", 1).WaitAsync(TimeSpan.FromSeconds(2))).Value);
        await session.SetExceptionBreakpoints([]).WaitAsync(TimeSpan.FromSeconds(2));

        fromAdapter.Dispose();
    }

    private static bool WaitFor(Func<bool> until, int milliseconds = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            if (until())
                return true;
            Thread.Sleep(10);
        }

        return until();
    }

    private static JsonObject ReadFramed(Stream from)
    {
        var header = new StringBuilder();
        int length = -1;
        while (true)
        {
            int b = from.ReadByte();
            if (b < 0)
                throw new EndOfStreamException();
            if (b == '\n')
            {
                string line = header.ToString().TrimEnd('\r');
                header.Clear();
                if (line.Length == 0 && length >= 0)
                    break;
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line[15..].Trim());
                continue;
            }
            header.Append((char)b);
        }

        byte[] body = new byte[length];
        from.ReadExactly(body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static void WriteFramed(Stream to, JsonObject message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        to.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        to.Write(body);
        to.Flush();
    }
}
