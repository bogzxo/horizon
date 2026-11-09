using System.Text.Json.Nodes;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    [Fact]
    public async Task ContinueAcknowledgementDoesNotDiscardAnInFlightPause()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch();
        adapter.Session.Pause();
        var threads = adapter.Read();
        Assert.Equal("threads", threads["command"]!.GetValue<string>());
        adapter.Event("continued", new JsonObject { ["threadId"] = 41 });
        adapter.Answer(threads, new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 41, ["name"] = "main" }) });
        var reading = Task.Run(adapter.Read);
        Assert.Same(reading, await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2))));
        var pause = await reading;
        Assert.Equal("pause", pause["command"]!.GetValue<string>());
        Assert.Equal(41, pause["arguments"]!["threadId"]!.GetValue<int>());
        adapter.Answer(pause);
        adapter.StopAt();
    }
}
