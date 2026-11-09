using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    [Fact]
    public async Task MemoryRequestsKeepNativeReferencesSignedOffsetsAndUnreadableBytes()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(memory: true);
        adapter.StopAt();
        var reading = adapter.Session.ReadMemory("native:opaque", -16, 8);
        var request = adapter.Read();
        Assert.Equal("readMemory", request["command"]!.GetValue<string>());
        Assert.Equal("native:opaque", request["arguments"]!["memoryReference"]!.GetValue<string>());
        Assert.Equal(-16, request["arguments"]!["offset"]!.GetValue<long>());
        Assert.Equal(8, request["arguments"]!["count"]!.GetValue<int>());
        adapter.Answer(request, JsonNode.Parse("""{"address":"0x1000","data":"SGV4AP8=","unreadableBytes":3}""")!.AsObject());
        var block = (await reading)!;
        Assert.Equal("0x1000", block.Address);
        Assert.Equal(new byte[] { 72, 101, 120, 0, 255 }, block.Data);
        Assert.Equal(3, block.UnreadableBytes);
        reading = adapter.Session.ReadMemory("0x0");
        adapter.Answer(adapter.Read(), JsonNode.Parse("""{"address":"0x0","unreadableBytes":256}""")!.AsObject());
        Assert.Empty((await reading)!.Data);
    }

    [Fact]
    public async Task VariableAndEvaluationMemoryReferencesSurviveNativeParsing()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(memory: true);
        adapter.StopAt();
        var variables = adapter.Session.Variables(7);
        adapter.Answer(adapter.Read(), JsonNode.Parse("""{"variables":[{"name":"bytes","value":"Hex","memoryReference":"opaque:1","variablesReference":0}]}""")!.AsObject());
        Assert.Equal("opaque:1", Assert.Single(await variables).MemoryReference);
        var evaluation = adapter.Session.TryEvaluate("bytes", 1);
        adapter.Answer(adapter.Read(), JsonNode.Parse("""{"result":"Hex","memoryReference":"opaque:2","variablesReference":0}""")!.AsObject());
        Assert.Equal("opaque:2", (await evaluation)!.Value.MemoryReference);
    }

    [Fact]
    public async Task MemoryCapabilityStateAndRequestBoundsPreventUnsupportedReads()
    {
        using var unsupported = new GotoAdapter();
        await unsupported.Launch();
        unsupported.StopAt();
        Assert.False(unsupported.Session.CanReadMemory);
        Assert.Null(await unsupported.Session.ReadMemory("0x1000"));
        unsupported.Session.StepOver();
        Assert.Equal("next", unsupported.Read()["command"]!.GetValue<string>());
        using var adapter = new GotoAdapter();
        await adapter.Launch(memory: true);
        Assert.Null(await adapter.Session.ReadMemory("0x1000"));
        adapter.StopAt();
        Assert.Null(await adapter.Session.ReadMemory(" "));
        Assert.Null(await adapter.Session.ReadMemory("0x1000", count: 0));
        Assert.Null(await adapter.Session.ReadMemory("0x1000", count: 4097));
        Assert.Null(await adapter.Session.ReadMemory("0x1000", offset: 9007199254740992));
        adapter.Session.StepOver();
        Assert.Equal("next", adapter.Read()["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task AStepAndReturnToTheSameFrameRetiresPendingMemory()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(memory: true);
        adapter.StopAt();
        var reading = adapter.Session.ReadMemory("0x1000");
        var old = adapter.Read();
        adapter.Session.StepOver();
        adapter.Answer(adapter.Read());
        adapter.StopAt();
        adapter.Answer(old, JsonNode.Parse("""{"address":"0x1000","data":"AQ=="}""")!.AsObject());
        Assert.Null(await reading);
    }

    [Fact]
    public async Task ThreadSelectionRetiresPendingMemory()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(memory: true);
        adapter.StopAt();
        var reading = adapter.Session.ReadMemory("0x1000");
        var old = adapter.Read();
        var selecting = adapter.Session.SelectThread(42);
        adapter.Answer(adapter.Read(), new JsonObject { ["stackFrames"] = new JsonArray() });
        await selecting;
        adapter.Answer(old, JsonNode.Parse("""{"address":"0x1000","data":"AQ=="}""")!.AsObject());
        Assert.Null(await reading);
    }

    [Theory]
    [InlineData("{\"address\":\"0x1\",\"data\":\"!!!!\"}")]
    [InlineData("{\"address\":\"0x1\",\"data\":\"AQID\"}")]
    [InlineData("{\"address\":\"0x1\",\"data\":\"AQIDBA==\"}")]
    [InlineData("{\"address\":\"0x1\",\"unreadableBytes\":-1}")]
    [InlineData("{\"data\":\"AQ==\"}")]
    public async Task MalformedNativeMemoryCannotProduceInventedOrOversizedSnapshots(string body)
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(memory: true);
        adapter.StopAt();
        var reading = adapter.Session.ReadMemory("0x1000", count: 1);
        adapter.Answer(adapter.Read(), JsonNode.Parse(body)!.AsObject());
        await Assert.ThrowsAsync<InvalidDataException>(() => reading);
        Assert.Equal(DebugState.Stopped, adapter.Session.State);
    }
}
