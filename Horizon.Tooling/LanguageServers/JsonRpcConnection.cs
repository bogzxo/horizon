using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>
/// JSON-RPC 2.0 over a pair of streams, framed the way the language server protocol frames it (a Content-Length
/// header, a blank line, the message), which is how a language server is talked to over its stdin and stdout. A
/// thread of its own reads what comes in and hands it out, replies to the requests this side made (<see cref="Request"/>),
/// notifications to whoever asked for them (<see cref="OnNotification"/>) and the server's own requests to
/// <see cref="OnRequest"/>, which answers null unless told otherwise, so a server asking to register a capability or
/// show a progress bar is never left waiting. Everything handed out is handed out on the reading thread, whoever
/// takes it marshals it to their own. Writing is under a lock, so any thread may send.
/// </summary>
public sealed class JsonRpcConnection : IDisposable
{
    /// <summary>What the other side said when it couldn't do what was asked.</summary>
    public sealed class RemoteException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private interface IPendingRequest
    {
        void SetError(Exception error);
        void SetJsonResult(JsonNode? result);
    }

    private interface IRawPendingRequest : IPendingRequest
    {
        void SetRawResult(ReadOnlyMemory<byte> rawResult);
    }

    private sealed class JsonPendingRequest(TaskCompletionSource<JsonNode?> tcs) : IPendingRequest
    {
        public void SetError(Exception error) => tcs.TrySetException(error);
        public void SetJsonResult(JsonNode? result) => tcs.TrySetResult(result);
    }

    private sealed class RawPendingRequest<T>(TaskCompletionSource<T> tcs, Func<ReadOnlyMemory<byte>, T> parseResult) : IRawPendingRequest
    {
        public void SetError(Exception error) => tcs.TrySetException(error);

        public void SetJsonResult(JsonNode? result)
        {
            if (result is null)
            {
                SetRawResult(ReadOnlyMemory<byte>.Empty);
                return;
            }
            SetRawResult(System.Text.Encoding.UTF8.GetBytes(result.ToJsonString()));
        }

        public void SetRawResult(ReadOnlyMemory<byte> rawResult)
        {
            try
            {
                tcs.TrySetResult(parseResult(rawResult));
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        }
    }

    private readonly Stream input, output;
    private readonly ContentLengthReader framing;
    private readonly ContentLengthWriter writer;
    private readonly Thread reader;
    private readonly Lock writing = new();
    private readonly ConcurrentDictionary<int, IPendingRequest> pending = new();
    private readonly Dictionary<string, Action<JsonNode?>> notifications = new(StringComparer.Ordinal);
    private int nextId;
    private int closedState;
    private int rawPendingCount;

    /// <summary>Answers the other side's requests, given the method and its params. Null answers null, which is what most want.</summary>
    public Func<string, JsonNode?, JsonNode?>? OnRequest { get; set; }

    /// <summary>
    /// Asked first about each of the other side's requests, a task for one that's answered later (a question for
    /// whoever is at the keyboard), which goes back when it's done while the reader goes on. Null for one that
    /// <see cref="OnRequest"/> answers on the spot.
    /// </summary>
    public Func<string, JsonNode?, Task<JsonNode?>?>? OnRequestLater { get; set; }

    /// <summary>Called once when the other side goes away or the stream breaks, with what broke it or null for a plain end.</summary>
    public Action<Exception?>? Closed { get; set; }

    /// <summary>Whether the other side has gone.</summary>
    public bool IsClosed => Volatile.Read(ref closedState) != 0;

    /// <param name="input">What the other side writes, read from here.</param>
    /// <param name="output">What this side writes to the other.</param>
    public JsonRpcConnection(Stream input, Stream output, string? traceName = null)
    {
        this.input = input;
        this.output = output;
        framing = new ContentLengthReader(input);
        writer = new ContentLengthWriter(output);
        trace = OpenTrace(traceName);
        reader = new Thread(Read) { IsBackground = true, Name = "json-rpc reader" };
        reader.Start();
    }

    // Every message both ways with the time it went, into a file of its own a connection in the folder HORIZON_LSP_TRACE
    // names, for finding out what a server was asked and what it said back. Nothing without it
    private readonly StreamWriter? trace;
    private readonly long traceStart = Environment.TickCount64;

    private static StreamWriter? OpenTrace(string? name)
    {
        if (Environment.GetEnvironmentVariable("HORIZON_LSP_TRACE") is not { Length: > 0 } folder)
            return null;
        try
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"{name ?? "lsp"}-{Environment.ProcessId}-{Environment.TickCount64}.log");
            return new StreamWriter(file, append: false, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Trace(string way, ReadOnlySpan<byte> body)
    {
        if (trace is null || IsClosed)
            return;
        string text = Encoding.UTF8.GetString(body);
        lock (trace)
        {
            if (IsClosed)
                return;
            try
            {
                trace.WriteLine($"{(Environment.TickCount64 - traceStart) / 1000.0,9:0.000} {way} {(text.Length > 600 ? text[..600] + "..." : text)}");
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Hands the notifications of a method to a handler, replacing any before it.</summary>
    public void OnNotification(string method, Action<JsonNode?> handler)
    {
        lock (notifications)
            notifications[method] = handler;
    }

    /// <summary>Sends a notification, which nobody answers.</summary>
    public void Notify(string method, JsonNode? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        Send(message);
    }

    /// <summary>Sends a request and comes back with its result, or throws what the other side answered with.</summary>
    public Task<JsonNode?> Request(string method, JsonNode? parameters)
    {
        int id = Interlocked.Increment(ref nextId);
        var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = new JsonPendingRequest(waiting);

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;

        try
        {
            Send(message);
        }
        catch (Exception e)
        {
            pending.TryRemove(id, out _);
            waiting.TrySetException(e);
        }

        return waiting.Task;
    }

    /// <summary>Sends a request and streams its raw UTF-8 result slice directly without allocating a <see cref="JsonNode"/> DOM.</summary>
    internal Task<T> RequestParsed<T>(string method, JsonNode? parameters, Func<ReadOnlyMemory<byte>, T> parseResult)
    {
        int id = Interlocked.Increment(ref nextId);
        var waiting = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = new RawPendingRequest<T>(waiting, parseResult);
        Interlocked.Increment(ref rawPendingCount);

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;

        try
        {
            Send(message);
        }
        catch (Exception e)
        {
            if (pending.TryRemove(id, out _))
                Interlocked.Decrement(ref rawPendingCount);
            waiting.TrySetException(e);
        }

        return waiting.Task;
    }

    private void Send(JsonObject message)
    {
        if (IsClosed)
            throw new InvalidOperationException("The other side has gone.");

        // Written node by node (the serializer proper wants reflection, which a trimmed application hasn't got) into a
        // buffer the writer keeps, so it's made under the lock that writes it
        lock (writing)
        {
            if (IsClosed)
                throw new InvalidOperationException("The other side has gone.");

            try
            {
                Trace(">>", writer.Make(message));
                writer.Write();
            }
            finally
            {
                writer.Settle();
            }
        }
    }

    // The reading thread, a message at a time until the stream ends
    private void Read()
    {
        Exception? broke = null;
        try
        {
            while (!IsClosed)
            {
                if (ReadMessage() is not { } body)
                    break;
                Trace("<<", body.Span);

                if (Volatile.Read(ref rawPendingCount) > 0 && TryDispatchRawResponse(body))
                    continue;

                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(body.Span);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message is JsonObject received)
                    Dispatch(received);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            broke = IsClosed ? null : e;
        }
        catch (Exception e)
        {
            broke = e;
        }

        Close(broke);
    }

    /// <summary>Helper method to read one framed message, null at the end of the stream, good until the next is read.</summary>
    private ReadOnlyMemory<byte>? ReadMessage()
    {
        // The headers, a line each, to the blank line, and a message without a length is an empty one
        if (framing.ReadHeaders(overBlankLinesFirst: false) is not int length)
            return null;
        return length < 0 ? ReadOnlyMemory<byte>.Empty : framing.ReadBody(length);
    }

    // Helper method to answer one of the other side's requests, nothing if the connection has gone meanwhile
    private void Answer(JsonNode id, JsonNode? result)
    {
        try
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
        }
        catch (Exception)
        {
            // Gone while it was being asked
        }
    }

    private void Dispatch(JsonObject message)
    {
        JsonNode? id = message["id"];
        string? method = message["method"]?.GetValue<string>();

        if (method is not null && id is not null)
        {
            // Their request, answered when it's done if it's answered later, the reader going on meanwhile
            Task<JsonNode?>? later = null;
            try
            {
                later = OnRequestLater?.Invoke(method, message["params"]);
            }
            catch (Exception)
            {
                // Answered on the spot instead
            }

            if (later is not null)
            {
                message.Remove("id");
                later.ContinueWith(done => Answer(id, done.IsCompletedSuccessfully ? done.Result : null), TaskScheduler.Default);
                return;
            }

            // Answered on the spot
            JsonNode? result = null;
            try
            {
                result = OnRequest?.Invoke(method, message["params"]);
            }
            catch (Exception)
            {
                // An answer they can't use is still better than none
            }

            try
            {
                message.Remove("id");
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            catch (Exception)
            {
                // Gone while answering, the reader finds that out next
            }

            return;
        }

        if (method is not null)
        {
            Action<JsonNode?>? handler;
            lock (notifications)
                notifications.TryGetValue(method, out handler);

            // One message that can't be made sense of is that message lost, not the server. A throw in here ended the
            // reader, and serve-d was gone for the run over one diagnostic
            try
            {
                handler?.Invoke(message["params"]);
            }
            catch (Exception)
            {
            }

            return;
        }

        // An answer to one of ours
        if (id is not null && id.GetValueKind() == JsonValueKind.Number && pending.TryRemove(JsonNumbers.Int(id, -1), out var waiting))
        {
            if (waiting is IRawPendingRequest)
                Interlocked.Decrement(ref rawPendingCount);

            if (message["error"] is JsonObject error)
                waiting.SetError(new RemoteException(JsonNumbers.Int(error["code"], 0), error["message"]?.GetValue<string>() ?? "no reason given"));
            else
                waiting.SetJsonResult(message["result"]);
        }
    }

    private bool TryDispatchRawResponse(ReadOnlyMemory<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            int? idValue = null;
            int resultStart = -1, resultEnd = -1;
            int errorStart = -1, errorEnd = -1;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("method"u8))
                    return false;

                if (reader.ValueTextEquals("id"u8))
                {
                    if (!reader.Read()) return false;
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int parsedId))
                        idValue = parsedId;
                    else if (!reader.TrySkip())
                        return false;
                    continue;
                }

                bool isResult = reader.ValueTextEquals("result"u8);
                bool isError = !isResult && reader.ValueTextEquals("error"u8);
                if (!reader.Read())
                    return false;

                int start = (int)reader.TokenStartIndex;
                if (!reader.TrySkip())
                    return false;
                int end = (int)reader.BytesConsumed;

                if (isResult)
                {
                    resultStart = start;
                    resultEnd = end;
                }
                else if (isError)
                {
                    errorStart = start;
                    errorEnd = end;
                }
            }

            if (idValue is not int responseId || !pending.TryGetValue(responseId, out IPendingRequest? waiting) || waiting is not IRawPendingRequest rawWaiting)
                return false;

            if (!pending.TryRemove(responseId, out _))
                return false;

            Interlocked.Decrement(ref rawPendingCount);
            if (errorStart >= 0 && JsonNode.Parse(body.Span[errorStart..errorEnd]) is JsonObject error)
            {
                rawWaiting.SetError(new RemoteException(JsonNumbers.Int(error["code"], 0), error["message"]?.GetValue<string>() ?? "no reason given"));
                return true;
            }

            rawWaiting.SetRawResult(resultStart >= 0 ? body.Slice(resultStart, resultEnd - resultStart) : ReadOnlyMemory<byte>.Empty);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void DrainPending(Exception error)
    {
        foreach (var (id, waiting) in pending)
        {
            if (pending.TryRemove(id, out _))
            {
                if (waiting is IRawPendingRequest)
                    Interlocked.Decrement(ref rawPendingCount);
                waiting.SetError(error);
            }
        }
    }

    private void Close(Exception? why)
    {
        if (Interlocked.Exchange(ref closedState, 1) != 0)
            return;

        DrainPending(why ?? new IOException("The other side has gone."));
        Closed?.Invoke(why);
    }

    public void Dispose()
    {
        bool firstClose = Interlocked.Exchange(ref closedState, 1) == 0;
        lock (writing)
        {
            try { output.Dispose(); } catch (Exception) { }
        }

        // On Linux and macOS letting go of a pipe waits for a read of it on another thread (the reading thread's) to
        // return, which is when the other side goes, so it's let go of off the caller's thread, or closing a language
        // server hung whoever closed it until the server's process was gone
        Task.Run(() =>
        {
            try { input.Dispose(); } catch (Exception) { }
        });

        DrainPending(new IOException("The other side has gone."));
        if (firstClose)
            Closed?.Invoke(null);

        if (trace is not null)
        {
            lock (trace)
            {
                try { trace.Dispose(); } catch (Exception) { }
            }
        }
    }
}
