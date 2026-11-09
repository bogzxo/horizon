using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    private static async Task<DebugInstruction> Instruction(GotoAdapter adapter, string address = "0x401000")
    {
        var reading = adapter.Session.Disassemble(address);
        adapter.Answer(adapter.Read(), new JsonObject { ["instructions"] = new JsonArray(new JsonObject { ["address"] = address, ["instruction"] = "mov eax, 1" }) });
        return Assert.Single((await reading)!);
    }

    [Fact]
    public async Task NativeInstructionBreakpointsPreserveDisabledEntriesAndVerification()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(disassemble: true, instructions: true);
        adapter.StopAt();
        var instruction = await Instruction(adapter);
        var adding = adapter.Session.AddInstructionBreakpoint(instruction);
        var request = adapter.Read();
        Assert.Equal("setInstructionBreakpoints", request["command"]!.GetValue<string>());
        Assert.Equal(instruction.Address, request["arguments"]!["breakpoints"]![0]!["instructionReference"]!.GetValue<string>());
        adapter.Answer(request, JsonNode.Parse("""{"breakpoints":[{"id":9,"verified":true}]}""")!.AsObject());
        Assert.True(await adding);
        Assert.True(Assert.Single(adapter.Session.InstructionBreakpoints).Verified);
        var disabling = adapter.Session.EnableInstructionBreakpoints(false);
        request = adapter.Read();
        Assert.Empty(request["arguments"]!["breakpoints"]!.AsArray());
        adapter.Answer(request, new JsonObject { ["breakpoints"] = new JsonArray() });
        Assert.True(await disabling);
        Assert.False(Assert.Single(adapter.Session.InstructionBreakpoints).Enabled);
        var enabling = adapter.Session.EnableInstructionBreakpoint(instruction.Address, true);
        request = adapter.Read();
        adapter.Answer(request, JsonNode.Parse("""{"breakpoints":[{"id":10,"verified":false,"message":"Native rejection"}]}""")!.AsObject());
        Assert.True(await enabling);
        var rejected = Assert.Single(adapter.Session.InstructionBreakpoints);
        Assert.False(rejected.Verified);
        Assert.Equal("Native rejection", rejected.Message);
        var removing = adapter.Session.ClearInstructionBreakpoints();
        request = adapter.Read();
        Assert.Empty(request["arguments"]!["breakpoints"]!.AsArray());
        adapter.Answer(request);
        Assert.True(await removing);
        Assert.Empty(adapter.Session.InstructionBreakpoints);
    }

    [Fact]
    public async Task InstructionOffersCannotBeReusedAfterResumeOrAcrossSessionsOrChangedToAnotherAddress()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(disassemble: true, instructions: true);
        adapter.StopAt();
        var instruction = await Instruction(adapter);
        Assert.False(await adapter.Session.AddInstructionBreakpoint(instruction with { Address = "0xdeadbeef" }));
        Assert.False(await adapter.Session.AddInstructionBreakpoint(new("0x401000", "mov", "", null)));
        using var other = new GotoAdapter();
        await other.Launch(disassemble: true, instructions: true);
        other.StopAt();
        Assert.False(await other.Session.AddInstructionBreakpoint(instruction));
        adapter.Session.StepOver();
        adapter.Answer(adapter.Read());
        adapter.StopAt(2);
        Assert.False(await adapter.Session.AddInstructionBreakpoint(instruction));
        adapter.Session.StepOver();
        Assert.Equal("next", adapter.Read()["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnsupportedAdaptersDoNotReceiveInstructionBreakpointRequests()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(disassemble: true);
        adapter.StopAt();
        var instruction = await Instruction(adapter);
        Assert.False(adapter.Session.CanSetInstructionBreakpoints);
        Assert.False(await adapter.Session.AddInstructionBreakpoint(instruction));
        Assert.False(await adapter.Session.ClearInstructionBreakpoints());
        adapter.Session.StepOver();
        Assert.Equal("next", adapter.Read()["command"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0x0000401000", "0x401000", true)]
    [InlineData("0xA", "0xa", true)]
    [InlineData("0xA", "0xB", false)]
    public void NativeAddressesCompareAcrossDisplayFormats(string first, string second, bool same) =>
        Assert.Equal(same, DebugSession.SameInstructionAddress(first, second));
}
