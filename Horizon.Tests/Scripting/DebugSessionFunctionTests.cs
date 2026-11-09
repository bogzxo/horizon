using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FunctionBreakpointsConfigureBeforeRunningAndTrackVerification(bool supported)
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);
        session.InitialFunctionBreakpoints = [" Program.Target ", "Program.Target"];
        var launching = session.Launch("fixture", [], Path.GetTempPath(), new Dictionary<string, IReadOnlyCollection<DebugBreakpoint>> { ["Program.cs"] = [new(3, Condition: "false")] });
        Answer(fromAdapter, ReadRequest(adapterIn), new JsonObject { ["supportsFunctionBreakpoints"] = supported });
        var launch = ReadRequest(adapterIn);
        Write(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "initialized" });
        var source = ReadRequest(adapterIn);
        Assert.Equal("setBreakpoints", source["command"]!.GetValue<string>());
        Answer(fromAdapter, source);
        if (supported)
        {
            var functions = ReadRequest(adapterIn);
            Assert.Equal("setFunctionBreakpoints", functions["command"]!.GetValue<string>());
            Assert.Equal("Program.Target", Assert.Single(functions["arguments"]!["breakpoints"]!.AsArray())!["name"]!.GetValue<string>());
            Answer(fromAdapter, functions, new JsonObject { ["breakpoints"] = new JsonArray(new JsonObject { ["id"] = 7, ["verified"] = false, ["message"] = "pending symbols" }) });
        }
        var done = ReadRequest(adapterIn);
        Assert.Equal("configurationDone", done["command"]!.GetValue<string>());
        Answer(fromAdapter, done);
        Answer(fromAdapter, launch);
        Assert.True(await launching.WaitAsync(TimeSpan.FromSeconds(5)));
        if (supported)
        {
            Assert.Equal("pending symbols", session.FunctionBreakpoint("Program.Target")!.Message);
            Write(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "breakpoint", ["body"] = new JsonObject { ["reason"] = "changed", ["breakpoint"] = new JsonObject
            {
                ["id"] = 7, ["verified"] = true, ["source"] = new JsonObject { ["path"] = "Program.cs" }, ["line"] = 10
            } } });
            Assert.True(WaitFor(() => session.FunctionBreakpoint("Program.Target")?.Verified == true));
            Assert.Equal(9, session.FunctionBreakpoint("Program.Target")!.Line);
            Assert.Equal("Program.cs", session.FunctionBreakpoint("Program.Target")!.File);
            // The adapter can report a function hit as a generic breakpoint at a conditional source breakpoint's line.
            Write(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "stopped", ["body"] = new JsonObject
            {
                ["reason"] = "breakpoint", ["threadId"] = 1, ["hitBreakpointIds"] = new JsonArray(7)
            } });
            Answer(fromAdapter, ReadRequest(adapterIn), new JsonObject { ["stackFrames"] = new JsonArray(new JsonObject
            {
                ["id"] = 1, ["name"] = "Program.Target", ["line"] = 4, ["source"] = new JsonObject { ["path"] = "Program.cs" }
            }) });
            Assert.True(WaitFor(() => session.State == DebugState.Stopped));
            Task removing = session.SetFunctionBreakpoints([]);
            var empty = ReadRequest(adapterIn);
            Assert.Equal("setFunctionBreakpoints", empty["command"]!.GetValue<string>());
            Assert.Empty(empty["arguments"]!["breakpoints"]!.AsArray());
            Answer(fromAdapter, empty, new JsonObject { ["breakpoints"] = new JsonArray() });
            await removing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(session.FunctionBreakpoint("Program.Target"));
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetFunctionBreakpoints(["Program.Target"]));
        fromAdapter.Dispose();
    }
}
