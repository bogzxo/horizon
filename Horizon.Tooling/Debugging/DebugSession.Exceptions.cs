using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public sealed record DebugExceptionFilter(string Id, string Label, string Description, bool Default);

public sealed partial class DebugSession
{
    public Func<DebugExceptionFilter, bool>? BreakOnException { get; set; }
    private HashSet<string> exceptionSelection = new(StringComparer.Ordinal);
    private bool windowsLldb;

    public IReadOnlyList<DebugExceptionFilter> ExceptionFilters => capabilities?["exceptionBreakpointFilters"] is JsonArray filters
        ? filters.OfType<JsonObject>().Where(filter => filter["filter"]?.GetValue<string>() is { Length: > 0 })
            .Select(filter => new DebugExceptionFilter(filter["filter"]!.GetValue<string>(),
                filter["label"]?.GetValue<string>() ?? filter["filter"]!.GetValue<string>(), filter["description"]?.GetValue<string>() ?? "",
                filter["default"]?.GetValue<bool>() == true)).ToArray() : [];

    public bool BreaksOnException(string filter) => exceptionSelection.Contains(filter);

    public async Task SetExceptionBreakpoints(IEnumerable<string> filters)
    {
        var selected = filters.ToHashSet(StringComparer.Ordinal);
        var available = ExceptionFilters.Select(filter => filter.Id).ToHashSet(StringComparer.Ordinal);
        if (!selected.IsSubsetOf(available)) throw new ArgumentException("The debugger does not offer this exception filter.", nameof(filters));
        var values = new JsonArray(selected.Order(StringComparer.Ordinal).Select(filter => (JsonNode?)JsonValue.Create(filter)).ToArray());
        await connection.Request("setExceptionBreakpoints", new JsonObject { ["filters"] = values }).ConfigureAwait(false);
        // LLDB's C++ language exception resolver targets the Itanium runtime. MSVC's runtime
        // throws through this exported function instead. Keep the native filter for MinGW programs.
        if (windowsLldb && Can("supportsFunctionBreakpoints"))
        {
            await functionUpdates.WaitAsync().ConfigureAwait(false);
            try
            {
                await SendFunctionBreakpoints(functionNames, selected).ConfigureAwait(false);
                exceptionSelection = selected;
            }
            finally { functionUpdates.Release(); }
        }
        exceptionSelection = selected;
    }

    private async Task ConfigureExceptions()
    {
        var filters = ExceptionFilters;
        if (filters.Count > 0)
            await SetExceptionBreakpoints(filters.Where(filter => BreakOnException?.Invoke(filter) ?? filter.Default).Select(filter => filter.Id)).ConfigureAwait(false);
    }
}
