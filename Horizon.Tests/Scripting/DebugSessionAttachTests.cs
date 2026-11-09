using System.Text.Json.Nodes;
using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    private static async Task Attach(GotoAdapter adapter)
    {
        var attaching = adapter.Session.Attach(123, new Dictionary<string, IReadOnlyCollection<int>>());
        var initialize = adapter.Read();
        Assert.Equal("initialize", initialize["command"]!.GetValue<string>());
        adapter.Answer(initialize);
        var attach = adapter.Read();
        Assert.Equal("attach", attach["command"]!.GetValue<string>());
        Assert.Equal(123, attach["arguments"]!["processId"]!.GetValue<int>());
        adapter.Event("initialized");
        adapter.Answer(adapter.Read());
        adapter.Answer(attach);
        Assert.True(await attaching.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttachStopPreservesExplicitProcessOwnershipAndSharesDisconnect(bool owned)
    {
        using var adapter = new GotoAdapter(owned);
        await Attach(adapter);
        adapter.StopAt();
        var stop = adapter.Session.StopAsync();
        Assert.Same(stop, adapter.Session.StopAsync());
        var request = adapter.Read();
        Assert.Equal("disconnect", request["command"]!.GetValue<string>());
        Assert.Equal(owned, request["arguments"]!["terminateDebuggee"]!.GetValue<bool>());
        Assert.False(request["arguments"]!["suspendDebuggee"]!.GetValue<bool>());
        adapter.Answer(request);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DebugState.Ended, adapter.Session.State);
        await adapter.Session.StopAsync();
    }

    [Fact]
    public async Task DisposeExternalSessionAcknowledgesDetachBeforeClosingTransport()
    {
        using var adapter = new GotoAdapter(terminateDebuggeeOnStop: false);
        await Attach(adapter);
        adapter.StopAt();
        var disposing = Task.Run(adapter.Session.Dispose);
        var request = adapter.Read();
        Assert.Equal("disconnect", request["command"]!.GetValue<string>());
        Assert.False(request["arguments"]!["terminateDebuggee"]!.GetValue<bool>());
        adapter.Answer(request);
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DebugState.Ended, adapter.Session.State);
    }

    [Fact]
    public async Task FailedExternalAttachmentStillDisconnectsWithoutTerminatingTheProgram()
    {
        using var adapter = new GotoAdapter(terminateDebuggeeOnStop: false);
        var attaching = adapter.Session.Attach(123, new Dictionary<string, IReadOnlyCollection<int>>());
        adapter.Answer(adapter.Read());
        adapter.Fail(adapter.Read());
        Assert.False(await attaching.WaitAsync(TimeSpan.FromSeconds(5)));
        var disposing = Task.Run(adapter.Session.Dispose);
        var disconnect = adapter.Read();
        Assert.Equal("disconnect", disconnect["command"]!.GetValue<string>());
        Assert.False(disconnect["arguments"]!["terminateDebuggee"]!.GetValue<bool>());
        adapter.Answer(disconnect);
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AdapterStartupCannotAttachToAReusedProcessIdentity()
    {
        using var adapter = new GotoAdapter();
        var attaching = adapter.Session.Attach(Environment.ProcessId, new Dictionary<string, IReadOnlyCollection<int>>(), expectedStartTime: DateTime.UnixEpoch);
        var initialize = adapter.Read();
        Assert.Equal("initialize", initialize["command"]!.GetValue<string>());
        adapter.Answer(initialize);
        Assert.False(await attaching.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("replaced", adapter.Session.Failed);
        Assert.Equal(DebugState.Ended, adapter.Session.State);
    }

    [Fact]
    public async Task PauseOnRuntimeHelperSelectsAnotherPausedThreadWithManagedFrames()
    {
        using var adapter = new GotoAdapter();
        await Attach(adapter);
        adapter.Event("stopped", new JsonObject { ["reason"] = "pause", ["threadId"] = 7 });
        var stack = adapter.Read();
        Assert.Equal(7, stack["arguments"]!["threadId"]!.GetValue<int>());
        adapter.Answer(stack, new JsonObject { ["stackFrames"] = new JsonArray() });
        var threads = adapter.Read();
        Assert.Equal("threads", threads["command"]!.GetValue<string>());
        adapter.Answer(threads, new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 7 }, new JsonObject { ["id"] = 41 }) });
        var managed = adapter.Read();
        Assert.Equal("stackTrace", managed["command"]!.GetValue<string>());
        Assert.Equal(41, managed["arguments"]!["threadId"]!.GetValue<int>());
        adapter.Answer(managed, new JsonObject { ["stackFrames"] = new JsonArray(new JsonObject { ["id"] = 9, ["name"] = "Main", ["line"] = 4 }) });
        DebugEvent stopped = default;
        Assert.True(WaitFor(() => adapter.Session.TryTake(out stopped) && stopped.Kind == DebugEventKind.Stopped));
        Assert.Equal("Main", Assert.Single(stopped.Frames).Name);
        Assert.Equal(41, adapter.Session.SelectedThread);
        adapter.Session.Continue();
        var resume = adapter.Read();
        Assert.Equal(41, resume["arguments"]!["threadId"]!.GetValue<int>());
        adapter.Answer(resume);
    }

    [Fact]
    public async Task DelayedPauseThreadFallbackCannotReviveAnEndedSession()
    {
        using var adapter = new GotoAdapter();
        await Attach(adapter);
        adapter.Event("stopped", new JsonObject { ["reason"] = "pause", ["threadId"] = 7 });
        adapter.Answer(adapter.Read(), new JsonObject { ["stackFrames"] = new JsonArray() });
        var threads = adapter.Read();
        var ending = adapter.Session.StopAsync();
        adapter.Answer(adapter.Read());
        await ending.WaitAsync(TimeSpan.FromSeconds(5));
        adapter.Answer(threads, new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 41 }) });
        await Task.Delay(30);
        Assert.Equal(DebugState.Ended, adapter.Session.State);
        while (adapter.Session.TryTake(out var next)) Assert.NotEqual(DebugEventKind.Stopped, next.Kind);
    }
}
