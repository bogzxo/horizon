using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public sealed record DebugSourceBreakpoint(int RequestedLine, int Line, bool Enabled, int Id = -1, bool Verified = false, string? Message = null);

/// <summary>A session-owned set of breakpoints for an adapter source, independent of any snapshot file.</summary>
public sealed class DebugSourceBreakpointSet
{
    internal DebugSession Owner { get; }
    internal DebugSourceBreakpoint[] Points = [];
    public DebugSource Source { get; internal set; }
    public IReadOnlyList<DebugSourceBreakpoint> Breakpoints => Owner.SourceBreakpointsOf(this);
    internal DebugSourceBreakpointSet(DebugSession owner, DebugSource source) => (Owner, Source) = (owner, source);
}

public sealed partial class DebugSession
{
    private readonly SemaphoreSlim sourceBreakpointUpdates = new(1, 1);
    private readonly List<DebugSourceBreakpointSet> sourceBreakpointSets = [];
    private readonly Dictionary<int, (long Number, JsonObject Point, string? Reason)> sourceBreakpointEvents = [];
    private long sourceBreakpointEventNumber;

    public IReadOnlyList<DebugSourceBreakpointSet> SourceBreakpointSets
    { get { lock (breakpoints) return sourceBreakpointSets.Where(set => set.Points.Length > 0).ToArray(); } }
    public bool HasSourceBreakpointSets { get { lock (breakpoints) return sourceBreakpointSets.Count > 0; } }

    internal IReadOnlyList<DebugSourceBreakpoint> SourceBreakpointsOf(DebugSourceBreakpointSet set)
    { lock (breakpoints) return set.Points.ToArray(); }

    public bool OwnsSourceBreakpoints(DebugSourceBreakpointSet set) => ReferenceEquals(set.Owner, this) && OwnsSource(set.Source);

    public DebugSourceBreakpointSet CreateSourceBreakpoints(DebugSource source)
    {
        if (!OwnsSource(source)) throw new ArgumentException("The source does not belong to an active session.", nameof(source));
        lock (breakpoints)
        {
            if (state == DebugState.Ended) throw new InvalidOperationException("The debug session has ended.");
            var existing = sourceBreakpointSets.FirstOrDefault(set => set.Source.Reference == source.Reference);
            if (existing is not null) return existing;
            var created = new DebugSourceBreakpointSet(this, source);
            sourceBreakpointSets.Add(created);
            return created;
        }
    }

    public Task<bool> AddSourceBreakpoint(DebugSourceBreakpointSet set, int line)
    {
        if (line < 0 || line == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(line));
        return ChangeSourceBreakpoints(set, points => points.Any(point => point.RequestedLine == line) ? points
            : [.. points.Append(new(line, line, true)).OrderBy(point => point.RequestedLine)]);
    }
    public Task<bool> ToggleSourceBreakpoint(DebugSourceBreakpointSet set, int line, bool enableOnly = false)
    {
        if (line < 0 || line == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(line));
        return ChangeSourceBreakpoints(set, points =>
        {
            var existing = points.FirstOrDefault(point => point.Line == line || point.RequestedLine == line);
            if (existing is null) return enableOnly ? points : [.. points.Append(new(line, line, true)).OrderBy(point => point.RequestedLine)];
            return enableOnly ? points.Select(point => point == existing ? point with { Enabled = !point.Enabled } : point).ToArray()
                : points.Where(point => point != existing).ToArray();
        });
    }
    public Task<bool> RemoveSourceBreakpoint(DebugSourceBreakpointSet set, int line) => ChangeSourceBreakpoints(set, points => points.Where(point => point.RequestedLine != line).ToArray());
    public Task<bool> EnableSourceBreakpoint(DebugSourceBreakpointSet set, int line, bool enabled) => ChangeSourceBreakpoints(set,
        points => points.Select(point => point.RequestedLine == line ? point with { Enabled = enabled } : point).ToArray());
    public Task<bool> RebindSourceBreakpoints(DebugSourceBreakpointSet set, DebugSource source) => ChangeSourceBreakpoints(set, points => points, source);

    public async Task<bool> EnableSourceBreakpoints(bool enabled)
    {
        foreach (var set in SourceBreakpointSets)
            if (!await ChangeSourceBreakpoints(set, points => points.Select(point => point with { Enabled = enabled }).ToArray()).ConfigureAwait(false)) return false;
        return true;
    }
    public async Task<bool> DeleteSourceBreakpoints()
    {
        foreach (var set in SourceBreakpointSets)
            if (!await ChangeSourceBreakpoints(set, _ => []).ConfigureAwait(false)) return false;
        return true;
    }

    private async Task<bool> ChangeSourceBreakpoints(DebugSourceBreakpointSet set, Func<DebugSourceBreakpoint[], DebugSourceBreakpoint[]> change, DebugSource? replacement = null)
    {
        if (!OwnsSourceBreakpoints(set) || state != DebugState.Stopped || replacement is not null && !OwnsSource(replacement)) return false;
        await sourceBreakpointUpdates.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!OwnsSourceBreakpoints(set) || state != DebugState.Stopped || replacement is not null && !OwnsSource(replacement)) return false;
            var source = set.Source;
            DebugSourceBreakpoint[] wanted;
            lock (breakpoints) wanted = change(set.Points);
            if (replacement is not null && replacement.Reference != source.Reference)
            {
                if (set.Points.Any(point => point.Enabled))
                    await connection.Request("setBreakpoints", new JsonObject { ["source"] = source.Description(), ["breakpoints"] = new JsonArray(), ["sourceModified"] = false }).ConfigureAwait(false);
                if (!OwnsSourceBreakpoints(set) || !OwnsSource(replacement)) return false;
                source = replacement;
                lock (breakpoints)
                {
                    if (state == DebugState.Ended) return false;
                    set.Source = source;
                    set.Points = set.Points.Select(point => point with { Id = -1, Verified = false }).ToArray();
                }
            }
            long events;
            lock (breakpoints) events = sourceBreakpointEventNumber;
            JsonNode? response = null;
            if (wanted.Any(point => point.Enabled) || set.Points.Any(point => point.Enabled))
                response = await connection.Request("setBreakpoints", new JsonObject
                {
                    ["source"] = source.Description(), ["sourceModified"] = false,
                    ["breakpoints"] = new JsonArray(wanted.Where(point => point.Enabled).Select(point => (JsonNode?)new JsonObject { ["line"] = point.RequestedLine + 1 }).ToArray())
                }).ConfigureAwait(false);
            if (!OwnsSourceBreakpoints(set)) return false;
            var results = response?["breakpoints"] as JsonArray;
            int index = 0;
            lock (breakpoints)
            {
                if (state == DebugState.Ended) return false;
                set.Source = source;
                set.Points = wanted.Select(point =>
                {
                    JsonNode? result = point.Enabled && index < (results?.Count ?? 0) ? results![index++] : null;
                    var placed = point with { Id = JsonNumbers.Int(result?["id"], -1), Verified = point.Enabled && result?["verified"] is JsonValue verified && verified.TryGetValue<bool>(out bool yes) && yes,
                        Line = Math.Max(0, JsonNumbers.Int(result?["line"], point.RequestedLine + 1) - 1), Message = Text(result?["message"]) };
                    if (placed.Id >= 0 && sourceBreakpointEvents.TryGetValue(placed.Id, out var moved) && moved.Number > events)
                        placed = SourceBreakpointEvent(placed, moved.Point, moved.Reason);
                    return placed;
                }).ToArray();
            }
            happened.Enqueue(new DebugEvent(DebugEventKind.BreakpointsChanged, "", [], 0));
            return true;
        }
        catch (Exception)
        {
            // The adapter may already have cleared the old source during rebind. Do not retain a solid marker.
            lock (breakpoints)
                if (state != DebugState.Ended) set.Points = set.Points.Select(point => point with { Verified = false }).ToArray();
            throw;
        }
        finally { sourceBreakpointUpdates.Release(); }
    }

    private static DebugSourceBreakpoint SourceBreakpointEvent(DebugSourceBreakpoint point, JsonObject moved, string? reason) => point with
    {
        Verified = reason != "removed" && moved["verified"] is JsonValue value && value.TryGetValue<bool>(out bool verified) && verified,
        Line = Math.Max(0, JsonNumbers.Int(moved["line"], point.Line + 1) - 1), Message = Text(moved["message"])
    };

    private void SourceReferenceBreakpointMoved(JsonNode? point, string? reason)
    {
        int id = JsonNumbers.Int(point?["id"], -1);
        if (id < 0 || point is not JsonObject moved) return;
        bool changed = false;
        lock (breakpoints)
        {
            if (state == DebugState.Ended) return;
            sourceBreakpointEvents[id] = (++sourceBreakpointEventNumber, (JsonObject)moved.DeepClone(), reason);
            foreach (var set in sourceBreakpointSets)
                for (int index = 0; index < set.Points.Length; index++)
                    if (set.Points[index].Id == id)
                    {
                        set.Points[index] = SourceBreakpointEvent(set.Points[index], moved, reason);
                        changed = true;
                    }
        }
        if (changed) happened.Enqueue(new DebugEvent(DebugEventKind.BreakpointsChanged, "", [], 0));
    }

    private bool HitSourceReferenceBreakpoint(JsonNode? body)
    {
        if (body?["hitBreakpointIds"] is not JsonArray ids) return false;
        lock (breakpoints) return ids.Any(id => sourceBreakpointSets.Any(set => set.Points.Any(point => point.Enabled && point.Id >= 0 && point.Id == JsonNumbers.Int(id, -1))));
    }

    private void ClearSourceReferenceBreakpoints()
    {
        lock (breakpoints)
        {
            foreach (var set in sourceBreakpointSets) set.Points = [];
            sourceBreakpointSets.Clear();
            sourceBreakpointEvents.Clear();
        }
    }
}
