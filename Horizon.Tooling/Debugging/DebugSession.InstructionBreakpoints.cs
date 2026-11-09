using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public sealed record DebugInstructionBreakpoint(string Address, string Description, bool Enabled, int Id = -1, bool Verified = false, string? Message = null);

public sealed partial class DebugSession
{
    private readonly SemaphoreSlim instructionUpdates = new(1, 1);
    private DebugInstructionBreakpoint[] placedInstructions = [];
    public bool CanSetInstructionBreakpoints => Can("supportsInstructionBreakpoints");
    public IReadOnlyList<DebugInstructionBreakpoint> InstructionBreakpoints { get { lock (breakpoints) return placedInstructions.ToArray(); } }
    public static bool SameInstructionAddress(string one, string two)
    {
        static bool Number(string value, out ulong address) => ulong.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(2) : value.AsSpan(),
            System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out address);
        return Number(one, out var first) && Number(two, out var second) ? first == second : string.Equals(one, two, StringComparison.OrdinalIgnoreCase);
    }

    private bool CurrentInstruction(DebugInstruction instruction) => ReferenceEquals(instruction.Owner, this) && State == DebugState.Stopped
        && instruction.Epoch == Volatile.Read(ref threadEpoch) && instruction.Selection == Volatile.Read(ref threadSelection)
        && instruction.OfferedAddress == instruction.Address && !string.IsNullOrEmpty(instruction.Address);

    public Task<bool> AddInstructionBreakpoint(DebugInstruction instruction) => ChangeInstructionBreakpoints(points =>
        points.Any(point => SameInstructionAddress(point.Address, instruction.Address)) ? points
            : [.. points, new(instruction.Address, instruction.Instruction, true)], instruction);
    public Task<bool> EnableInstructionBreakpoint(string address, bool enabled) => ChangeInstructionBreakpoints(points =>
        points.Select(point => SameInstructionAddress(point.Address, address) ? point with { Enabled = enabled } : point).ToArray());
    public Task<bool> RemoveInstructionBreakpoint(string address) => ChangeInstructionBreakpoints(points =>
        points.Where(point => !SameInstructionAddress(point.Address, address)).ToArray());
    public Task<bool> EnableInstructionBreakpoints(bool enabled) => ChangeInstructionBreakpoints(points => points.Select(point => point with { Enabled = enabled }).ToArray());
    public Task<bool> ClearInstructionBreakpoints() => ChangeInstructionBreakpoints(_ => []);

    private async Task<bool> ChangeInstructionBreakpoints(Func<DebugInstructionBreakpoint[], DebugInstructionBreakpoint[]> change, DebugInstruction? adding = null)
    {
        if (!CanSetInstructionBreakpoints || State != DebugState.Stopped || adding is {} initial && !CurrentInstruction(initial)) return false;
        await instructionUpdates.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DebugState.Stopped || adding is {} instruction && !CurrentInstruction(instruction)) return false;
            DebugInstructionBreakpoint[] wanted;
            lock (breakpoints) wanted = change(placedInstructions);
            var body = await connection.Request("setInstructionBreakpoints", new JsonObject
            {
                ["breakpoints"] = new JsonArray(wanted.Where(point => point.Enabled).Select(point => (JsonNode?)new JsonObject { ["instructionReference"] = point.Address }).ToArray())
            }).ConfigureAwait(false);
            var placed = body?["breakpoints"] as JsonArray;
            int index = 0;
            lock (breakpoints)
                placedInstructions = wanted.Select(point =>
                {
                    JsonNode? result = point.Enabled && index < (placed?.Count ?? 0) ? placed![index++] : null;
                    return point with { Id = JsonNumbers.Int(result?["id"], -1), Verified = result?["verified"]?.GetValue<bool>() == true, Message = result?["message"]?.GetValue<string>() };
                }).ToArray();
            happened.Enqueue(new DebugEvent(DebugEventKind.BreakpointsChanged, "", [], 0));
            return true;
        }
        finally { instructionUpdates.Release(); }
    }

    private bool HitInstructionBreakpoint(JsonNode? body)
    {
        if (body?["hitBreakpointIds"] is not JsonArray ids) return false;
        lock (breakpoints) return ids.Any(id => placedInstructions.Any(point => point.Enabled && point.Id >= 0 && point.Id == JsonNumbers.Int(id, -1)));
    }

    private void InstructionBreakpointMoved(JsonNode? point)
    {
        int id = JsonNumbers.Int(point?["id"], -1);
        if (id < 0) return;
        lock (breakpoints)
        {
            int index = Array.FindIndex(placedInstructions, old => old.Id == id);
            if (index < 0) return;
            placedInstructions[index] = placedInstructions[index] with
            { Verified = point?["verified"]?.GetValue<bool>() == true, Message = point?["message"]?.GetValue<string>() };
        }
        happened.Enqueue(new DebugEvent(DebugEventKind.BreakpointsChanged, "", [], 0));
    }
}
