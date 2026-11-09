using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

public readonly record struct DebugInstruction(string Address, string Instruction, string Bytes, string? Symbol)
{
    internal DebugSession? Owner { get; init; }
    internal long Epoch { get; init; }
    internal long Selection { get; init; }
    internal string? OfferedAddress { get; init; }
}

public sealed partial class DebugSession
{
    public bool CanDisassemble => Can("supportsDisassembleRequest");
    public bool CanStepInstruction => Can("supportsSteppingGranularity");

    /// <summary>The native instructions beginning at a paused frame's instruction pointer.</summary>
    public async Task<IReadOnlyList<DebugInstruction>?> Disassemble(string reference, int count = 48)
    {
        if (!CanDisassemble || State != DebugState.Stopped || string.IsNullOrWhiteSpace(reference) || count is < 1 or > 256) return null;
        long epoch = Volatile.Read(ref threadEpoch), selection = Volatile.Read(ref threadSelection);
        var body = await connection.Request("disassemble", new JsonObject
        {
            ["memoryReference"] = reference, ["instructionCount"] = count, ["resolveSymbols"] = true
        }).ConfigureAwait(false);
        if (State != DebugState.Stopped || epoch != Volatile.Read(ref threadEpoch) || selection != Volatile.Read(ref threadSelection)) return null;
        return body?["instructions"] is JsonArray instructions
            ? instructions.OfType<JsonObject>().Select(item => new DebugInstruction(item["address"]?.GetValue<string>() ?? string.Empty,
                item["instruction"]?.GetValue<string>() ?? string.Empty, item["instructionBytes"]?.GetValue<string>() ?? string.Empty,
                item["symbol"]?.GetValue<string>()) { Owner = this, Epoch = epoch, Selection = selection, OfferedAddress = item["address"]?.GetValue<string>() })
                .Where(item => item.Address.Length > 0 && item.Instruction.Length > 0).ToArray()
            : [];
    }

    public void StepInstructionOver() { if (CanStepInstruction) Go("next", granularity: "instruction"); }
    public void StepInstructionIn() { if (CanStepInstruction) Go("stepIn", granularity: "instruction"); }
}
