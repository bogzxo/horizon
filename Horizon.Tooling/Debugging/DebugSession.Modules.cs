using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>A DAP module ID; numeric and string IDs with the same spelling identify different modules.</summary>
public readonly record struct DebugModuleId(string Value, bool Numeric);

/// <summary>Native module information. Empty strings and null booleans mean the adapter did not report a value.</summary>
public sealed record DebugModule(DebugModuleId Id, string Name)
{
    public string Path { get; init; } = "";
    public string SymbolStatus { get; init; } = "";
    public string SymbolFilePath { get; init; } = "";
    public string Version { get; init; } = "";
    public string AddressRange { get; init; } = "";
    public string DateTimeStamp { get; init; } = "";
    public bool? Optimized { get; init; }
    public bool? UserCode { get; init; }
}

public sealed partial class DebugSession
{
    private readonly Lock modulesLock = new();
    private Dictionary<DebugModuleId, DebugModule> loadedModules = [];
    private readonly Dictionary<DebugModuleId, long> moduleChanges = [];
    private long moduleEvents, moduleRequest, modulesRevision;

    public bool CanRequestModules => Can("supportsModulesRequest");
    public int LoadedModuleCount { get { lock (modulesLock) return loadedModules.Count; } }
    public long ModulesRevision => Volatile.Read(ref modulesRevision);
    public IReadOnlyList<DebugModule> LoadedModules
    {
        get
        {
            lock (modulesLock)
                return loadedModules.Values.OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(module => module.Path, StringComparer.OrdinalIgnoreCase).ThenBy(module => module.Id.Value, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Refresh a complete native snapshot, preserving module events that arrive during the query.</summary>
    public async Task<bool> RefreshModules(CancellationToken cancel = default)
    {
        long request, events;
        lock (modulesLock)
        {
            if (!CanRequestModules || state is DebugState.Starting or DebugState.Ended) return false;
            request = ++moduleRequest;
            events = moduleEvents;
        }
        var response = await connection.Request("modules", new JsonObject { ["startModule"] = 0, ["moduleCount"] = 0 }).WaitAsync(cancel).ConfigureAwait(false);
        if (response is not JsonObject body || body["modules"] is not JsonArray list)
            throw new InvalidDataException("The debugger did not return a module list.");
        var snapshot = new Dictionary<DebugModuleId, DebugModule>();
        foreach (var item in list.OfType<JsonObject>())
            if (ModuleId(item["id"]) is {} id && ReadModule(item, id) is {} module) snapshot[id] = module;
        lock (modulesLock)
        {
            if (cancel.IsCancellationRequested || state == DebugState.Ended || request != moduleRequest) return false;
            // Events are newer than the snapshot. Keep their replacements and tombstones, including unloads.
            foreach (var (id, changed) in moduleChanges)
                if (changed > events)
                {
                    if (loadedModules.TryGetValue(id, out var current)) snapshot[id] = current;
                    else snapshot.Remove(id);
                }
            if (snapshot.Count != loadedModules.Count || snapshot.Any(pair => !loadedModules.TryGetValue(pair.Key, out var old) || old != pair.Value))
            {
                loadedModules = snapshot;
                Interlocked.Increment(ref modulesRevision);
            }
            return true;
        }
    }

    private void ModuleChanged(JsonNode? body)
    {
        if (body is not JsonObject change || change["module"] is not JsonObject item || ModuleId(item["id"]) is not {} id) return;
        lock (modulesLock)
        {
            if (state == DebugState.Ended) return;
            switch (Text(change["reason"]))
            {
                case "removed":
                    moduleChanges[id] = ++moduleEvents;
                    if (loadedModules.Remove(id)) Interlocked.Increment(ref modulesRevision);
                    break;
                case "new":
                case "changed":
                    loadedModules.TryGetValue(id, out var previous);
                    if (ReadModule(item, id, previous) is not {} module) return;
                    moduleChanges[id] = ++moduleEvents;
                    if (previous != module)
                    {
                        loadedModules[id] = module;
                        Interlocked.Increment(ref modulesRevision);
                    }
                    break;
            }
        }
    }

    private static DebugModuleId? ModuleId(JsonNode? id)
    {
        if (id is not JsonValue value) return null;
        if (value.GetValueKind() == JsonValueKind.String) return new(value.GetValue<string>(), false);
        if (value.GetValueKind() != JsonValueKind.Number) return null;
        // Decimal canonicalization keeps 1 and 1.0 the same without merging large integer identifiers.
        string number = value.ToJsonString();
        if (decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)) number = parsed.ToString("G29", CultureInfo.InvariantCulture);
        return new(number, true);
    }

    private static DebugModule? ReadModule(JsonObject item, DebugModuleId id, DebugModule? previous = null)
    {
        string Field(string name, string? fallback) => item.ContainsKey(name) ? Text(item[name]) ?? "" : fallback ?? "";
        bool? Flag(string name, bool? fallback) => !item.ContainsKey(name) ? fallback : item[name] is JsonValue value
            && value.TryGetValue<bool>(out bool flag) ? flag : null;
        string name = Field("name", previous?.Name);
        return name.Length == 0 ? null : new(id, name)
        {
            Path = Field("path", previous?.Path), SymbolStatus = Field("symbolStatus", previous?.SymbolStatus),
            SymbolFilePath = Field("symbolFilePath", previous?.SymbolFilePath), Version = Field("version", previous?.Version),
            AddressRange = Field("addressRange", previous?.AddressRange), DateTimeStamp = Field("dateTimeStamp", previous?.DateTimeStamp),
            Optimized = Flag("isOptimized", previous?.Optimized), UserCode = Flag("isUserCode", previous?.UserCode)
        };
    }

    private void ClearModules()
    {
        lock (modulesLock)
        {
            moduleRequest++;
            loadedModules.Clear();
            moduleChanges.Clear();
            Interlocked.Increment(ref modulesRevision);
        }
    }
}
