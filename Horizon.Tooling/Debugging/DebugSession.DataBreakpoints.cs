using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>A data ID obtained from one paused frame. Only its owning session can install it.</summary>
public sealed class DebugDataBreakpointInfo
{
    internal DebugDataBreakpointInfo(DebugSession owner, long epoch, long selection, string? dataId, string description, IReadOnlyList<string> accessTypes)
        => (Owner, Epoch, Selection, DataId, Description, AccessTypes) = (owner, epoch, selection, dataId, description, accessTypes);
    internal DebugSession Owner { get; }
    internal long Epoch { get; }
    internal long Selection { get; }
    public string? DataId { get; }
    public string Description { get; }
    public IReadOnlyList<string> AccessTypes { get; }
}

public sealed record DebugDataBreakpoint(string DataId, string Description, string AccessType, bool Enabled, int Id = -1, bool Verified = false, string? Message = null);

public sealed partial class DebugSession
{
    private readonly SemaphoreSlim dataUpdates = new(1, 1);
    private DebugDataBreakpoint[] placedData = [];
    public bool CanSetDataBreakpoints => Can("supportsDataBreakpoints");
    public IReadOnlyList<DebugDataBreakpoint> DataBreakpoints { get { lock (breakpoints) return placedData.ToArray(); } }

    public async Task<DebugDataBreakpointInfo?> DataBreakpointInfo(int reference, string name, int frame)
    {
        if (!CanSetDataBreakpoints || State != DebugState.Stopped || reference <= 0) return null;
        long epoch = Volatile.Read(ref threadEpoch), selection = Volatile.Read(ref threadSelection);
        var body = await connection.Request("dataBreakpointInfo", new JsonObject
        {
            ["variablesReference"] = reference, ["name"] = name, ["frameId"] = frame
        }).ConfigureAwait(false);
        var access = body?["accessTypes"] is JsonArray list ? list.OfType<JsonValue>().Select(value => value.GetValue<string>())
            .Where(value => value is "read" or "write" or "readWrite").Distinct().ToArray() : ["write"];
        var found = new DebugDataBreakpointInfo(this, epoch, selection, body?["dataId"]?.GetValue<string>(),
            body?["description"]?.GetValue<string>() ?? name, Array.AsReadOnly(access));
        return CurrentDataInfo(found) ? found : null;
    }

    private bool CurrentDataInfo(DebugDataBreakpointInfo info) => ReferenceEquals(info.Owner, this) && State == DebugState.Stopped
        && info.Epoch == Volatile.Read(ref threadEpoch) && info.Selection == Volatile.Read(ref threadSelection);

    public Task<bool> AddDataBreakpoint(DebugDataBreakpointInfo info, string accessType = "write")
    {
        if (info.DataId is not { Length: > 0 } || !info.AccessTypes.Contains(accessType)) return Task.FromResult(false);
        return ChangeDataBreakpoints(points => points.Any(point => point.DataId == info.DataId) ? points
            : [.. points, new(info.DataId, info.Description, accessType, true)], info);
    }

    public Task<bool> EnableDataBreakpoint(string dataId, bool enabled) => ChangeDataBreakpoints(points =>
        points.Select(point => point.DataId == dataId ? point with { Enabled = enabled } : point).ToArray());
    public Task<bool> RemoveDataBreakpoint(string dataId) => ChangeDataBreakpoints(points => points.Where(point => point.DataId != dataId).ToArray());
    public Task<bool> EnableDataBreakpoints(bool enabled) => ChangeDataBreakpoints(points => points.Select(point => point with { Enabled = enabled }).ToArray());
    public Task<bool> ClearDataBreakpoints() => ChangeDataBreakpoints(_ => []);

    private async Task<bool> ChangeDataBreakpoints(Func<DebugDataBreakpoint[], DebugDataBreakpoint[]> change, DebugDataBreakpointInfo? adding = null)
    {
        if (!CanSetDataBreakpoints || State != DebugState.Stopped) return false;
        await dataUpdates.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DebugState.Stopped || adding is not null && !CurrentDataInfo(adding)) return false;
            DebugDataBreakpoint[] wanted;
            lock (breakpoints) wanted = change(placedData);
            var active = wanted.Where(point => point.Enabled).ToArray();
            var list = new JsonArray(active.Select(point => (JsonNode?)new JsonObject { ["dataId"] = point.DataId, ["accessType"] = point.AccessType }).ToArray());
            var body = await connection.Request("setDataBreakpoints", new JsonObject { ["breakpoints"] = list }).ConfigureAwait(false);
            var placed = body?["breakpoints"] as JsonArray;
            int index = 0;
            lock (breakpoints)
                placedData = wanted.Select(point =>
                {
                    JsonNode? result = point.Enabled && index < (placed?.Count ?? 0) ? placed![index++] : null;
                    return point with { Id = JsonNumbers.Int(result?["id"], -1), Verified = result?["verified"]?.GetValue<bool>() == true, Message = result?["message"]?.GetValue<string>() };
                }).ToArray();
            happened.Enqueue(new DebugEvent(DebugEventKind.BreakpointsChanged, "", [], 0));
            return true;
        }
        finally { dataUpdates.Release(); }
    }

    private bool HitDataBreakpoint(JsonNode? body)
    {
        if (body?["hitBreakpointIds"] is not JsonArray ids) return false;
        lock (breakpoints) return ids.Any(id => placedData.Any(point => point.Enabled && point.Id >= 0 && point.Id == JsonNumbers.Int(id, -1)));
    }

    private void DataBreakpointMoved(JsonNode? point)
    {
        int id = JsonNumbers.Int(point?["id"], -1);
        if (id < 0) return;
        lock (breakpoints)
        {
            int index = Array.FindIndex(placedData, old => old.Id == id);
            if (index < 0) return;
            placedData[index] = placedData[index] with
            {
                Verified = point?["verified"]?.GetValue<bool>() == true, Message = point?["message"]?.GetValue<string>()
            };
        }
        happened.Enqueue(new DebugEvent(DebugEventKind.BreakpointsChanged, "", [], 0));
    }
}
