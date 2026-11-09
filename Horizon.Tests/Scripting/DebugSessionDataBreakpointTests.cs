using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    [Fact]
    public async Task DataBreakpointsSerializeNativeSetsAndExposeVerificationFailures()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(data: true);
        adapter.StopAt();
        async Task<DebugDataBreakpointInfo> Info(string id)
        {
            var asking = adapter.Session.DataBreakpointInfo(7, id, 1);
            var request = adapter.Read();
            Assert.Equal("dataBreakpointInfo", request["command"]!.GetValue<string>());
            Assert.Equal(7, request["arguments"]!["variablesReference"]!.GetValue<int>());
            Assert.Equal(1, request["arguments"]!["frameId"]!.GetValue<int>());
            adapter.Answer(request, new JsonObject { ["dataId"] = id, ["description"] = id + " memory", ["accessTypes"] = new JsonArray("read", "write") });
            return Assert.IsType<DebugDataBreakpointInfo>(await asking);
        }
        var a = await Info("a");
        var b = await Info("b");
        var addA = adapter.Session.AddDataBreakpoint(a);
        var first = adapter.Read();
        var addB = adapter.Session.AddDataBreakpoint(b);
        Assert.Equal("setDataBreakpoints", first["command"]!.GetValue<string>());
        Assert.Equal("a", first["arguments"]!["breakpoints"]![0]!["dataId"]!.GetValue<string>());
        adapter.Answer(first, new JsonObject { ["breakpoints"] = new JsonArray(new JsonObject { ["id"] = 11, ["verified"] = true }) });
        Assert.True(await addA);
        var second = adapter.Read();
        Assert.Equal(2, second["arguments"]!["breakpoints"]!.AsArray().Count);
        adapter.Answer(second, new JsonObject { ["breakpoints"] = new JsonArray(new JsonObject { ["id"] = 12, ["verified"] = true },
            new JsonObject { ["id"] = 13, ["verified"] = false, ["message"] = "hardware slots exhausted" }) });
        Assert.True(await addB);
        Assert.True(adapter.Session.DataBreakpoints[0].Verified);
        Assert.Equal("hardware slots exhausted", adapter.Session.DataBreakpoints[1].Message);
        var disable = adapter.Session.EnableDataBreakpoint("a", false);
        var disabled = adapter.Read();
        Assert.Equal("b", Assert.Single(disabled["arguments"]!["breakpoints"]!.AsArray())!["dataId"]!.GetValue<string>());
        adapter.Answer(disabled, new JsonObject { ["breakpoints"] = new JsonArray(new JsonObject { ["id"] = 14, ["verified"] = true }) });
        Assert.True(await disable);
        Assert.False(adapter.Session.DataBreakpoints[0].Enabled);
        Assert.True(adapter.Session.DataBreakpoints[1].Verified);
        var remove = adapter.Session.RemoveDataBreakpoint("b");
        var removed = adapter.Read();
        Assert.Empty(removed["arguments"]!["breakpoints"]!.AsArray());
        adapter.Answer(removed, new JsonObject { ["breakpoints"] = new JsonArray() });
        Assert.True(await remove);
        Assert.Equal("a", Assert.Single(adapter.Session.DataBreakpoints).DataId);
        var enable = adapter.Session.EnableDataBreakpoints(true);
        var enabled = adapter.Read();
        Assert.Single(enabled["arguments"]!["breakpoints"]!.AsArray());
        adapter.Answer(enabled, new JsonObject { ["breakpoints"] = new JsonArray(new JsonObject { ["id"] = 15, ["verified"] = true }) });
        Assert.True(await enable);
        adapter.Event("breakpoint", new JsonObject { ["reason"] = "changed", ["breakpoint"] = new JsonObject
            { ["id"] = 15, ["verified"] = false, ["message"] = "watchpoint no longer available" } });
        Assert.True(WaitFor(() => adapter.Session.DataBreakpoints[0].Message == "watchpoint no longer available"));
        Assert.False(adapter.Session.DataBreakpoints[0].Verified);
        var clear = adapter.Session.ClearDataBreakpoints();
        var cleared = adapter.Read();
        Assert.Empty(cleared["arguments"]!["breakpoints"]!.AsArray());
        adapter.Answer(cleared, new JsonObject { ["breakpoints"] = new JsonArray() });
        Assert.True(await clear);
        Assert.Empty(adapter.Session.DataBreakpoints);
    }

    [Fact]
    public async Task DataBreakpointCreationRejectsStaleReferencesAndUnsupportedVariables()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(data: true);
        adapter.StopAt();
        var unavailable = adapter.Session.DataBreakpointInfo(7, "constant", 1);
        adapter.Answer(adapter.Read(), new JsonObject { ["dataId"] = null, ["description"] = "not stored in memory" });
        var missing = Assert.IsType<DebugDataBreakpointInfo>(await unavailable);
        Assert.Null(missing.DataId);
        Assert.Equal("not stored in memory", missing.Description);
        Assert.False(await adapter.Session.AddDataBreakpoint(missing));
        var asking = adapter.Session.DataBreakpointInfo(7, "value", 1);
        adapter.Answer(adapter.Read(), new JsonObject { ["dataId"] = "address/4", ["description"] = "value" });
        var ready = Assert.IsType<DebugDataBreakpointInfo>(await asking);
        using var other = new GotoAdapter();
        Assert.False(await other.Session.AddDataBreakpoint(ready));
        adapter.Session.StepOver();
        adapter.Answer(adapter.Read());
        adapter.StopAt(2);
        Assert.False(await adapter.Session.AddDataBreakpoint(ready));
        var delayed = adapter.Session.DataBreakpointInfo(7, "value", 1);
        var query = adapter.Read();
        adapter.Session.StepOver();
        adapter.Answer(adapter.Read());
        adapter.StopAt(3);
        adapter.Answer(query, new JsonObject { ["dataId"] = "old-address/4", ["description"] = "value" });
        Assert.Null(await delayed);
        adapter.Session.StepOver();
        Assert.Equal("next", adapter.Read()["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task AQueuedDataBreakpointCannotUseAReferenceAfterExecutionResumes()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(data: true);
        adapter.StopAt();
        async Task<DebugDataBreakpointInfo> Info(string id)
        {
            var query = adapter.Session.DataBreakpointInfo(7, id, 1);
            adapter.Answer(adapter.Read(), new JsonObject { ["dataId"] = id, ["description"] = id });
            return (await query)!;
        }
        var a = await Info("a");
        var b = await Info("b");
        var first = adapter.Session.AddDataBreakpoint(a);
        var pending = adapter.Read();
        var queued = adapter.Session.AddDataBreakpoint(b);
        adapter.Session.StepOver();
        var step = adapter.Read();
        Assert.Equal("next", step["command"]!.GetValue<string>());
        adapter.Answer(step);
        adapter.Answer(pending, new JsonObject { ["breakpoints"] = new JsonArray(new JsonObject { ["id"] = 1, ["verified"] = true }) });
        Assert.True(await first);
        Assert.False(await queued);
        Assert.Equal("a", Assert.Single(adapter.Session.DataBreakpoints).DataId);
    }

    [Fact]
    public async Task DataBreakpointCapabilityAndWriteAccessAreRequired()
    {
        using var unsupported = new GotoAdapter();
        await unsupported.Launch();
        unsupported.StopAt();
        Assert.False(unsupported.Session.CanSetDataBreakpoints);
        Assert.Null(await unsupported.Session.DataBreakpointInfo(7, "value", 1));
        using var adapter = new GotoAdapter();
        await adapter.Launch(data: true);
        adapter.StopAt();
        var asking = adapter.Session.DataBreakpointInfo(7, "value", 1);
        adapter.Answer(adapter.Read(), new JsonObject { ["dataId"] = "address/4", ["description"] = "value", ["accessTypes"] = new JsonArray("read") });
        Assert.False(await adapter.Session.AddDataBreakpoint((await asking)!));
        adapter.Session.StepOver();
        Assert.Equal("next", adapter.Read()["command"]!.GetValue<string>());
    }
}
