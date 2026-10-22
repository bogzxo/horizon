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

    private readonly Stream input, output;
    private readonly Thread reader;
    private readonly Lock writing = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly Dictionary<string, Action<JsonNode?>> notifications = new(StringComparer.Ordinal);
    private int nextId;
    private volatile bool closed;

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
    public bool IsClosed => closed;

    /// <param name="input">What the other side writes, read from here.</param>
    /// <param name="output">What this side writes to the other.</param>
    public JsonRpcConnection(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
        reader = new Thread(Read) { IsBackground = true, Name = "json-rpc reader" };
        reader.Start();
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
        pending[id] = waiting;

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

    private void Send(JsonObject message)
    {
        if (closed)
            throw new InvalidOperationException("The other side has gone.");

        // Written node by node, the serializer proper wants reflection, which a trimmed application hasn't got
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            message.WriteTo(writer);
        ReadOnlySpan<byte> body = buffer.WrittenSpan;
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        lock (writing)
        {
            output.Write(header);
            output.Write(body);
            output.Flush();
        }
    }

    // The reading thread, a message at a time until the stream ends
    private void Read()
    {
        Exception? broke = null;
        try
        {
            while (!closed)
            {
                byte[]? body = ReadMessage();
                if (body is null)
                    break;

                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(body);
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
            broke = closed ? null : e;
        }
        catch (Exception e)
        {
            broke = e;
        }

        Close(broke);
    }

    /// <summary>Helper method to read one framed message, null at the end of the stream.</summary>
    private byte[]? ReadMessage()
    {
        int length = -1;
        var line = new StringBuilder();

        // The headers, a line each, to the blank line
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0)
                return null;

            if (b == '\n')
            {
                string header = line.ToString().TrimEnd('\r');
                line.Clear();
                if (header.Length == 0)
                    break;

                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(header.AsSpan(15).Trim(), out int parsed))
                    length = parsed;
                continue;
            }

            line.Append((char)b);
        }

        if (length < 0)
            return [];

        byte[] body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int got = input.Read(body, read, length - read);
            if (got <= 0)
                return null;
            read += got;
        }

        return body;
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
                JsonNode answering = id.DeepClone();
                later.ContinueWith(done => Answer(answering, done.IsCompletedSuccessfully ? done.Result : null), TaskScheduler.Default);
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
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
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
            handler?.Invoke(message["params"]);
            return;
        }

        // An answer to one of ours
        if (id is not null && id.GetValueKind() == JsonValueKind.Number && pending.TryRemove(id.GetValue<int>(), out var waiting))
        {
            if (message["error"] is JsonObject error)
                waiting.TrySetException(new RemoteException(error["code"]?.GetValue<int>() ?? 0, error["message"]?.GetValue<string>() ?? "no reason given"));
            else
                waiting.TrySetResult(message["result"]);
        }
    }

    private void Close(Exception? why)
    {
        if (closed)
            return;

        closed = true;
        foreach (var (id, waiting) in pending)
        {
            pending.TryRemove(id, out _);
            waiting.TrySetException(why ?? new IOException("The other side has gone."));
        }

        Closed?.Invoke(why);
    }

    public void Dispose()
    {
        closed = true;
        try { output.Dispose(); } catch (Exception) { }

        // On Linux and macOS letting go of a pipe waits for a read of it on another thread (the reading thread's) to
        // return, which is when the other side goes, so it's let go of off the caller's thread, or closing a language
        // server hung whoever closed it until the server's process was gone
        Task.Run(() =>
        {
            try { input.Dispose(); } catch (Exception) { }
        });
        Close(null);
    }
}
