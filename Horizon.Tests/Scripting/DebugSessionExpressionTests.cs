using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetExpressionRequiresStoppedCapabilityAndUsesSelectedFrame(bool supported)
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);
        var launching = session.Launch("fixture", [], Path.GetTempPath(), new Dictionary<string, IReadOnlyCollection<int>>());
        Answer(fromAdapter, ReadRequest(adapterIn), new JsonObject { ["supportsSetExpression"] = supported });
        var launch = ReadRequest(adapterIn);
        Write(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "initialized" });
        Answer(fromAdapter, ReadRequest(adapterIn));
        Answer(fromAdapter, launch);
        Assert.True(await launching.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(supported, session.CanSetExpression);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetExpression("box.Value", "100", 42));
        Write(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "stopped", ["body"] = new JsonObject { ["reason"] = "breakpoint", ["threadId"] = 1 } });
        Answer(fromAdapter, ReadRequest(adapterIn), new JsonObject { ["stackFrames"] = new JsonArray(new JsonObject { ["id"] = 42, ["name"] = "Main", ["line"] = 1 }) });
        Assert.True(WaitFor(() => session.State == DebugState.Stopped));
        if (supported)
        {
            Task assigning = session.SetExpression("box.Value", "100", 42);
            var request = ReadRequest(adapterIn);
            Assert.Equal("setExpression", request["command"]!.GetValue<string>());
            Assert.Equal("box.Value", request["arguments"]!["expression"]!.GetValue<string>());
            Assert.Equal("100", request["arguments"]!["value"]!.GetValue<string>());
            Assert.Equal(42, request["arguments"]!["frameId"]!.GetValue<int>());
            Answer(fromAdapter, request, new JsonObject { ["value"] = "100" });
            await assigning.WaitAsync(TimeSpan.FromSeconds(5));
            Task failed = session.SetExpression("box.Value", "bad", 42);
            var refused = ReadRequest(adapterIn);
            Write(fromAdapter, new JsonObject { ["type"] = "response", ["command"] = "setExpression", ["request_seq"] = refused["seq"]!.DeepClone(), ["success"] = false, ["message"] = "invalid value" });
            var error = await Assert.ThrowsAnyAsync<Exception>(() => failed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("invalid value", error.Message);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetExpression("box.Value", "100", 42));
        fromAdapter.Dispose();
    }
}
