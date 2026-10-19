using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>
/// The Debug Adapter Protocol over a pair of streams, VS Code's way of talking to a debugger (netcoredbg, a debugger for
/// anything else), framed like the language server protocol (a Content-Length header, a blank line, the message) but its
/// own messages, a <c>seq</c> each, requests with a <c>command</c> and <c>arguments</c>, responses saying whether they
/// went and a <c>body</c>, and events. A thread of its own reads what comes in, answers to this side's requests go back to
/// whoever asked (<see cref="Request"/>), events to <see cref="OnEvent"/>, the adapter's own requests (a terminal to run
/// in, which this side doesn't do) are answered that they didn't go. Everything is handed out on the reading thread.
/// </summary>
public sealed class DebugAdapterConnection : IDisposable
{
    /// <summary>What the adapter said when a request didn't go.</summary>
    public sealed class FailedException(string command, string message) : Exception(message)
    {
        public string Command { get; } = command;
    }

    private readonly Stream input, output;
    private readonly Thread reader;
    private readonly Lock writing = new();
    private readonly ConcurrentDictionary<int, (string Command, TaskCompletionSource<JsonNode?> Waiting)> pending = new();
    private int nextSeq;
    private volatile bool closed;

    /// <summary>An event, its name and body, on the reading thread.</summary>
    public Action<string, JsonNode?>? OnEvent { get; set; }

    /// <summary>Called once when the adapter goes away or the stream breaks, with what broke it or null for a plain end.</summary>
    public Action<Exception?>? Closed { get; set; }

    /// <summary>Whether the adapter has gone.</summary>
    public bool IsClosed => closed;

    public DebugAdapterConnection(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
        reader = new Thread(Read) { IsBackground = true, Name = "debug adapter reader" };
        reader.Start();
    }

    /// <summary>Sends a request and comes back with the body of its response, or throws what the adapter said when it didn't go.</summary>
    public Task<JsonNode?> Request(string command, JsonNode? arguments = null)
    {
        int seq = Interlocked.Increment(ref nextSeq);
        var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[seq] = (command, waiting);

        var message = new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command };
        if (arguments is not null)
            message["arguments"] = arguments;

        try
        {
            Send(message);
        }
        catch (Exception e)
        {
            pending.TryRemove(seq, out _);
            waiting.TrySetException(e);
        }

        return waiting.Task;
    }

    private void Send(JsonObject message)
    {
        if (closed)
            throw new InvalidOperationException("The debugger has gone.");

        // Node by node, the serializer proper wants reflection a trimmed application hasn't got
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

    // One framed message, null at the end of the stream
    private byte[]? ReadMessage()
    {
        int length = -1;
        var line = new StringBuilder();
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
                {
                    // a debugger that writes a line of its own before the first header (netcoredbg can) is let say it
                    if (length < 0)
                        continue;
                    break;
                }

                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(header.AsSpan(15).Trim(), out int parsed))
                    length = parsed;
                continue;
            }

            line.Append((char)b);
        }

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

    private void Dispatch(JsonObject message)
    {
        switch (message["type"]?.GetValue<string>())
        {
            case "response":
                if (message["request_seq"] is JsonValue asked && asked.TryGetValue(out int seq) && pending.TryRemove(seq, out var waiting))
                {
                    if (message["success"]?.GetValue<bool>() == true)
                        waiting.Waiting.TrySetResult(message["body"]);
                    else
                        waiting.Waiting.TrySetException(new FailedException(waiting.Command, message["message"]?.GetValue<string>() ?? "it didn't go"));
                }
                break;

            case "event":
                OnEvent?.Invoke(message["event"]?.GetValue<string>() ?? string.Empty, message["body"]);
                break;

            case "request":
                // The adapter asking for something of ours (runInTerminal), which this side doesn't do
                try
                {
                    Send(new JsonObject
                    {
                        ["seq"] = Interlocked.Increment(ref nextSeq),
                        ["type"] = "response",
                        ["request_seq"] = message["seq"]?.DeepClone(),
                        ["command"] = message["command"]?.DeepClone(),
                        ["success"] = false,
                        ["message"] = "not done here"
                    });
                }
                catch (Exception)
                {
                    // gone while answering, the reader finds that out next
                }
                break;
        }
    }

    private void Close(Exception? why)
    {
        if (closed && pending.IsEmpty)
        {
            Closed?.Invoke(why);
            Closed = null;
            return;
        }

        closed = true;
        foreach (var (seq, waiting) in pending)
        {
            pending.TryRemove(seq, out _);
            waiting.Waiting.TrySetException(why ?? new IOException("The debugger has gone."));
        }

        Closed?.Invoke(why);
        Closed = null;
    }

    public void Dispose()
    {
        closed = true;
        try { input.Dispose(); } catch (Exception) { }
        try { output.Dispose(); } catch (Exception) { }
        Close(null);
    }
}
