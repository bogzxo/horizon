using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public sealed record DebugFunctionBreakpoint(string Name, int Id, bool Verified, string? Message, string? File, int Line);

public sealed partial class DebugSession
{
    public IReadOnlyList<string> InitialFunctionBreakpoints { get; set; } = [];
    private readonly SemaphoreSlim functionUpdates = new(1, 1);
    private string[] functionNames = [];
    private DebugFunctionBreakpoint[] placedFunctions = [];

    public DebugFunctionBreakpoint? FunctionBreakpoint(string name)
    {
        lock (breakpoints) return placedFunctions.FirstOrDefault(point => point.Name == name);
    }

    private bool HitFunctionBreakpoint(JsonNode? body)
    {
        if (body?["hitBreakpointIds"] is not JsonArray ids) return false;
        lock (breakpoints)
            return ids.Any(id => placedFunctions.Any(point => point.Id >= 0 && point.Id == JsonNumbers.Int(id, -1)));
    }

    public async Task SetFunctionBreakpoints(IEnumerable<string> names)
    {
        if (!Can("supportsFunctionBreakpoints")) throw new InvalidOperationException("The debugger does not support function breakpoints.");
        string[] wanted = names.Select(name => name.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        await functionUpdates.WaitAsync().ConfigureAwait(false);
        try
        {
            await SendFunctionBreakpoints(wanted, exceptionSelection).ConfigureAwait(false);
            functionNames = wanted;
        }
        finally { functionUpdates.Release(); }
    }

    private async Task SendFunctionBreakpoints(string[] wanted, IReadOnlySet<string> exceptions)
    {
        var list = new JsonArray(wanted.Select(name => (JsonNode?)new JsonObject { ["name"] = name }).ToArray());
        if (windowsLldb && exceptions.Contains("cpp_throw") && !wanted.Contains("_CxxThrowException", StringComparer.Ordinal))
            list.Add((JsonNode)new JsonObject { ["name"] = "_CxxThrowException" });
        JsonNode? response = await connection.Request("setFunctionBreakpoints", new JsonObject { ["breakpoints"] = list }).ConfigureAwait(false);
        JsonArray? placed = response?["breakpoints"] as JsonArray;
        lock (breakpoints)
            placedFunctions = wanted.Select((name, index) => FunctionPoint(name, index < (placed?.Count ?? 0) ? placed![index] : null)).ToArray();
        happened.Enqueue(new DebugEvent(DebugEventKind.BreakpointsChanged, "", [], 0));
    }

    private static DebugFunctionBreakpoint FunctionPoint(string name, JsonNode? point) => new(name,
        JsonNumbers.Int(point?["id"], -1), point?["verified"]?.GetValue<bool>() == true, point?["message"]?.GetValue<string>(),
        point?["source"]?["path"]?.GetValue<string>(), JsonNumbers.Int(point?["line"], 0) - 1);

    private void FunctionMoved(JsonNode? point)
    {
        int id = JsonNumbers.Int(point?["id"], -1);
        if (id < 0) return;
        bool changed = false;
        lock (breakpoints)
        {
            for (int i = 0; i < placedFunctions.Length; i++)
                if (placedFunctions[i].Id == id)
                {
                    var old = placedFunctions[i];
                    var updated = FunctionPoint(old.Name, point);
                    placedFunctions[i] = updated with { File = updated.File ?? old.File, Line = updated.Line >= 0 ? updated.Line : old.Line };
                    changed = true;
                }
        }
        if (changed) happened.Enqueue(new DebugEvent(DebugEventKind.BreakpointsChanged, "", [], 0));
    }
}
