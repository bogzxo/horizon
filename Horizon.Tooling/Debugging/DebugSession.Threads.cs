using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public readonly record struct DebugThread(int Id, string Name);

/// <summary>A stack awaiting acceptance by the caller. Loading it does not change the thread used for stepping.</summary>
public sealed class DebugThreadSelection
{
    internal DebugThreadSelection(DebugSession owner, int thread, long epoch, long request, IReadOnlyList<DebugFrame> frames)
        => (Owner, Thread, Epoch, Request, Frames) = (owner, thread, epoch, request, frames);
    internal DebugSession Owner { get; }
    internal long Epoch { get; }
    internal long Request { get; }
    public int Thread { get; }
    public IReadOnlyList<DebugFrame> Frames { get; }
}

public sealed partial class DebugSession
{
    private long threadEpoch, threadSelection;
    private TaskCompletionSource? threadBaseline;
    public int SelectedThread => stoppedThread;

    public async Task<IReadOnlyList<DebugThread>> Threads()
    {
        if (threadBaseline is { } baseline) await baseline.Task.ConfigureAwait(false);
        if (await connection.Request("threads").ConfigureAwait(false) is not JsonObject body || body["threads"] is not JsonArray threads) return [];
        return threads.OfType<JsonObject>().Select(thread => new DebugThread(JsonNumbers.Int(thread["id"], 0), thread["name"]?.GetValue<string>() ?? "?")).ToArray();
    }

    public async Task<IReadOnlyList<DebugFrame>?> SelectThread(int thread)
    {
        var selection = await PrepareThreadSelection(thread).ConfigureAwait(false);
        return selection is not null && TrySelectThread(selection) ? selection.Frames : null;
    }

    public async Task<DebugThreadSelection?> PrepareThreadSelection(int thread)
    {
        if (State != DebugState.Stopped) return null;
        long epoch = Volatile.Read(ref threadEpoch), selection = Interlocked.Increment(ref threadSelection);
        var frames = await StackTrace(thread).ConfigureAwait(false);
        if (State != DebugState.Stopped || epoch != Volatile.Read(ref threadEpoch) || selection != Volatile.Read(ref threadSelection)) return null;
        return new(this, thread, epoch, selection, frames);
    }

    /// <summary>Commit only after the consumer has accepted the stack; obsolete stops and requests are rejected.</summary>
    public bool TrySelectThread(DebugThreadSelection selection)
    {
        if (!ReferenceEquals(selection.Owner, this) || State != DebugState.Stopped
            || selection.Epoch != Volatile.Read(ref threadEpoch) || selection.Request != Volatile.Read(ref threadSelection)) return false;
        stoppedThread = selection.Thread;
        return true;
    }

    private async Task<IReadOnlyList<DebugFrame>> StackTrace(int thread)
    {
        var frames = new List<DebugFrame>();
        if (await connection.Request("stackTrace", new JsonObject { ["threadId"] = thread, ["levels"] = 50 }).ConfigureAwait(false) is JsonObject body && body["stackFrames"] is JsonArray list)
            foreach (JsonObject one in list.OfType<JsonObject>())
                frames.Add(new DebugFrame(JsonNumbers.Int(one["id"], 0), one["name"]?.GetValue<string>() ?? "?", one["source"]?["path"]?.GetValue<string>(),
                    Math.Max(0, JsonNumbers.Int(one["line"], 1) - 1), Math.Max(0, JsonNumbers.Int(one["column"], 1) - 1))
                {
                    InstructionPointerReference = one["instructionPointerReference"]?.GetValue<string>(),
                    Source = one["source"] is JsonObject source && JsonNumbers.Int(source["sourceReference"], 0) > 0 ? new DebugSource(this, source) : null
                });
        return frames;
    }
}
