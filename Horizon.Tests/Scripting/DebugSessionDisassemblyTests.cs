using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    [Fact]
    public async Task NativeDisassemblyPreservesInstructionsBytesSymbolsAndFramePointers()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(disassemble: true);
        adapter.StopAt();
        var selecting = adapter.Session.SelectThread(41);
        adapter.Answer(adapter.Read(), JsonNode.Parse("""{"stackFrames":[{"id":7,"name":"native","instructionPointerReference":"0x401000"}]}""")!.AsObject());
        Assert.Equal("0x401000", Assert.Single((await selecting)!).InstructionPointerReference);
        var reading = adapter.Session.Disassemble("0x401000");
        var request = adapter.Read();
        Assert.Equal("disassemble", request["command"]!.GetValue<string>());
        Assert.Equal("0x401000", request["arguments"]!["memoryReference"]!.GetValue<string>());
        Assert.Equal(48, request["arguments"]!["instructionCount"]!.GetValue<int>());
        Assert.True(request["arguments"]!["resolveSymbols"]!.GetValue<bool>());
        adapter.Answer(request, JsonNode.Parse("""{"instructions":[{"address":"0x401000","instruction":"mov eax, 1","instructionBytes":"B8 01 00 00 00","symbol":"main"},{"instruction":"unavailable"}]}""")!.AsObject());
        var found = Assert.Single((await reading)!);
        Assert.Equal(("0x401000", "mov eax, 1", "B8 01 00 00 00", "main"), (found.Address, found.Instruction, found.Bytes, found.Symbol));
    }

    [Fact]
    public async Task InstructionStepsUseNativeGranularityAndRetireOldDisassembly()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(disassemble: true);
        Assert.Null(await adapter.Session.Disassemble("0x401000"));
        adapter.StopAt();
        Assert.Null(await adapter.Session.Disassemble(""));
        Assert.Null(await adapter.Session.Disassemble("0x401000", 257));
        var reading = adapter.Session.Disassemble("0x401000");
        var old = adapter.Read();
        adapter.Session.StepInstructionOver();
        var step = adapter.Read();
        Assert.Equal("next", step["command"]!.GetValue<string>());
        Assert.Equal("instruction", step["arguments"]!["granularity"]!.GetValue<string>());
        Assert.Equal(41, step["arguments"]!["threadId"]!.GetValue<int>());
        adapter.Answer(step);
        adapter.StopAt(2);
        adapter.Answer(old, new JsonObject { ["instructions"] = new JsonArray() });
        Assert.Null(await reading);
        adapter.Session.StepInstructionIn();
        step = adapter.Read();
        Assert.Equal("stepIn", step["command"]!.GetValue<string>());
        Assert.Equal("instruction", step["arguments"]!["granularity"]!.GetValue<string>());
        adapter.Answer(step);
    }

    [Fact]
    public async Task ThreadChangesRetireDisassemblyAndUnsupportedAdaptersNeverReceiveInstructionRequests()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(disassemble: true);
        adapter.StopAt();
        var reading = adapter.Session.Disassemble("0x401000");
        var old = adapter.Read();
        var selecting = adapter.Session.SelectThread(42);
        adapter.Answer(adapter.Read(), new JsonObject { ["stackFrames"] = new JsonArray() });
        await selecting;
        adapter.Answer(old, new JsonObject { ["instructions"] = new JsonArray() });
        Assert.Null(await reading);
        using var unsupported = new GotoAdapter();
        await unsupported.Launch();
        unsupported.StopAt();
        Assert.False(unsupported.Session.CanDisassemble);
        Assert.Null(await unsupported.Session.Disassemble("0x401000"));
        unsupported.Session.StepInstructionOver();
        unsupported.Session.StepOver();
        var step = unsupported.Read();
        Assert.Equal("next", step["command"]!.GetValue<string>());
        Assert.Null(step["arguments"]!["granularity"]);
        unsupported.Answer(step);
    }
}
