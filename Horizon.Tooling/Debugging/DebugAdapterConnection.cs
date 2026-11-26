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
    private readonly ContentLengthReader framing;
    private readonly ContentLengthWriter writer;
    private readonly Thread reader;
    private readonly Lock writing = new();
    private readonly ConcurrentDictionary<int, (string Command, TaskCompletionSource<JsonNode?> Waiting)> pending = new();
    private int nextSeq;
    private int closedState;

    /// <summary>An event, its name and body, on the reading thread.</summary>
    public Action<string, JsonNode?>? OnEvent { get; set; }

    /// <summary>Called once when the adapter goes away or the stream breaks, with what broke it or null for a plain end.</summary>
    public Action<Exception?>? Closed { get; set; }

    /// <summary>Whether the adapter has gone.</summary>
    public bool IsClosed => Volatile.Read(ref closedState) != 0;

    public DebugAdapterConnection(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
        framing = new ContentLengthReader(input);
        writer = new ContentLengthWriter(output);
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
        if (IsClosed)
            throw new InvalidOperationException("The debugger has gone.");

        // Node by node (the serializer proper wants reflection a trimmed application hasn't got) into a buffer the
        // writer keeps, so it's made under the lock that writes it
        lock (writing)
        {
            if (IsClosed)
                throw new InvalidOperationException("The debugger has gone.");

            try
            {
                writer.Make(message);
                writer.Write();
            }
            finally
            {
                writer.Settle();
            }
        }
    }

    private void Read()
    {
        Exception? broke = null;
        try
        {
            while (!IsClosed)
            {
                if (ReadMessage() is not { } body)
                    break;

                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(Utf8Only(body.Span));
                }
                catch (JsonException)
                {
                    continue;
                }

                // One message this side couldn't make sense of is that message lost, not the session. A string that
                // wasn't UTF-8 threw when it was read, out of here, and the whole debug session ended with it
                try
                {
                    if (message is JsonObject received)
                        Dispatch(received);
                }
                catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or ArgumentException or KeyNotFoundException)
                {
                    // the next one's read as if this one never came
                }
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

    /// <summary>
    /// A message as UTF-8 the JSON reader takes, what wasn't UTF-8 made the replacement character. netcoredbg on Windows
    /// writes what a program printed in the console's code page, Horizon's "1600×900" came over as a lone 0xFF byte, and
    /// reading that string threw on the reading thread and ended the debug session with nothing said.
    /// </summary>
    internal static ReadOnlySpan<byte> Utf8Only(ReadOnlySpan<byte> body) =>
        System.Text.Unicode.Utf8.IsValid(body) ? body : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body));

    // One framed message, null at the end of the stream, good until the next is read. A debugger that writes a line of
    // its own before the first header (netcoredbg can) is let say it
    private ReadOnlyMemory<byte>? ReadMessage()
    {
        if (framing.ReadHeaders(overBlankLinesFirst: true) is not int length)
            return null;
        return length < 0 ? ReadOnlyMemory<byte>.Empty : framing.ReadBody(length);
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
                message.Remove("seq", out JsonNode? requestSeq);
                message.Remove("command", out JsonNode? requestCommand);
                try
                {
                    Send(new JsonObject
                    {
                        ["seq"] = Interlocked.Increment(ref nextSeq),
                        ["type"] = "response",
                        ["request_seq"] = requestSeq,
                        ["command"] = requestCommand,
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

    private void DrainPending(Exception error)
    {
        foreach (var (seq, waiting) in pending)
        {
            if (pending.TryRemove(seq, out _))
                waiting.Waiting.TrySetException(error);
        }
    }

    private void Close(Exception? why)
    {
        if (Interlocked.Exchange(ref closedState, 1) != 0)
            return;

        DrainPending(why ?? new IOException("The debugger has gone."));
        Closed?.Invoke(why);
        Closed = null;
    }

    public void Dispose()
    {
        bool firstClose = Interlocked.Exchange(ref closedState, 1) == 0;
        lock (writing)
        {
            try { output.Dispose(); } catch (Exception) { }
        }

        // On Linux and macOS letting go of a pipe waits for a read of it on another thread to return, the reading
        // thread's, which is when the debugger goes, so it's let go of off the caller's thread (JsonRpcConnection too)
        Task.Run(() =>
        {
            try { input.Dispose(); } catch (Exception) { }
        });

        DrainPending(new IOException("The debugger has gone."));
        if (firstClose)
        {
            Closed?.Invoke(null);
            Closed = null;
        }
    }
}
