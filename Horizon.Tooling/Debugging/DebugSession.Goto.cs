using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public readonly record struct DebugGotoTarget(int Id, string Label, int Line, int Column);

/// <summary>Native execution targets belonging to one paused thread and one request.</summary>
public sealed class DebugGotoTargets
{
    internal DebugGotoTargets(DebugSession owner, long epoch, long selection, long request, int thread, IReadOnlyList<DebugGotoTarget> targets)
        => (Owner, Epoch, Selection, Request, Thread, Targets) = (owner, epoch, selection, request, thread, targets);
    internal DebugSession Owner { get; }
    internal long Epoch { get; }
    internal long Selection { get; }
    internal long Request { get; }
    public int Thread { get; }
    public IReadOnlyList<DebugGotoTarget> Targets { get; }
}

public sealed partial class DebugSession
{
    private long gotoRequest;
    public bool CanGoto => Can("supportsGotoTargetsRequest");

    /// <summary>Asks the adapter for exact-line targets without changing execution.</summary>
    public async Task<DebugGotoTargets?> PrepareGoto(string file, int line)
    {
        if (!CanGoto || State != DebugState.Stopped || stoppedThread < 0 || line < 0) return null;
        long epoch = Volatile.Read(ref threadEpoch), selection = Volatile.Read(ref threadSelection), request = Interlocked.Increment(ref gotoRequest);
        int thread = stoppedThread;
        var body = await connection.Request("gotoTargets", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = file }, ["line"] = line + 1
        }).ConfigureAwait(false);
        var targets = new List<DebugGotoTarget>();
        if (body?["targets"] is JsonArray list)
            foreach (JsonObject item in list.OfType<JsonObject>())
                if (JsonNumbers.Int(item["id"], -1) is >= 0 and var id && JsonNumbers.Int(item["line"], -1) == line + 1)
                    targets.Add(new(id, item["label"]?.GetValue<string>() ?? "?", line, Math.Max(0, JsonNumbers.Int(item["column"], 1) - 1)));
        var found = new DebugGotoTargets(this, epoch, selection, request, thread, targets.AsReadOnly());
        return IsCurrentGoto(found) ? found : null;
    }

    private bool IsCurrentGoto(DebugGotoTargets found) => ReferenceEquals(found.Owner, this) && CanGoto && State == DebugState.Stopped
        && found.Thread == stoppedThread && found.Epoch == Volatile.Read(ref threadEpoch)
        && found.Selection == Volatile.Read(ref threadSelection) && found.Request == Volatile.Read(ref gotoRequest);

    /// <summary>Moves the instruction pointer to an offered target; skipped code is not executed.</summary>
    public async Task<bool> Goto(DebugGotoTargets found, int target)
    {
        if (!IsCurrentGoto(found) || !found.Targets.Any(item => item.Id == target)) return false;
        Interlocked.Increment(ref gotoRequest);
        Interlocked.Increment(ref threadEpoch);
        await connection.Request("goto", new JsonObject { ["threadId"] = found.Thread, ["targetId"] = target }).ConfigureAwait(false);
        // The adapter's stopped/goto event supplies the new stack, scopes and execution line.
        return true;
    }
}
