using System.IO.Pipes;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests;

public partial class DebugSessionTests
{
    private sealed class GotoAdapter : IDisposable
    {
        private readonly AnonymousPipeServerStream to = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream from = new(PipeDirection.Out);
        private readonly AnonymousPipeClientStream input, output;
        public DebugSession Session { get; }
        public GotoAdapter(bool terminateDebuggeeOnStop = true)
        {
            input = new(PipeDirection.In, to.ClientSafePipeHandle);
            output = new(PipeDirection.In, from.ClientSafePipeHandle);
            Session = DebugSession.Over(output, to, terminateDebuggeeOnStop);
        }
        public JsonObject Read() => ReadRequest(input);
        public void Event(string name, JsonNode? body = null) => Write(from, new JsonObject { ["type"] = "event", ["event"] = name, ["body"] = body });
        public void Answer(JsonObject request, JsonObject? body = null) => DebugSessionTests.Answer(from, request, body);
        public void Fail(JsonObject request) => Write(from, new JsonObject { ["type"] = "response", ["request_seq"] = request["seq"]!.DeepClone(),
            ["command"] = request["command"]!.DeepClone(), ["success"] = false, ["message"] = "invalid jump" });
        public static JsonObject Targets(params (int Id, int Line)[] targets) => new() { ["targets"] = new JsonArray(targets.Select(target => (JsonNode?)new JsonObject
            { ["id"] = target.Id, ["label"] = "target " + target.Id, ["line"] = target.Line, ["column"] = 3 }).ToArray()) };
        public async Task Launch(bool capable = true, bool complete = false, string kind = DebugSession.CORECLR, bool data = false, bool disassemble = false, bool instructions = false, bool memory = false, bool modules = false)
        {
            var launching = Session.Launch("fixture", [], Path.GetTempPath(), new Dictionary<string, IReadOnlyCollection<int>>(), kind);
            Answer(Read(), new JsonObject { ["supportsGotoTargetsRequest"] = capable, ["supportsCompletionsRequest"] = complete, ["supportsDataBreakpoints"] = data,
                ["supportsDisassembleRequest"] = disassemble, ["supportsSteppingGranularity"] = disassemble, ["supportsInstructionBreakpoints"] = instructions, ["supportsReadMemoryRequest"] = memory, ["supportsModulesRequest"] = modules });
            var launch = Read();
            Write(from, new JsonObject { ["type"] = "event", ["event"] = "initialized" });
            Answer(Read());
            Answer(launch);
            if (kind == "lldb-dap") Answer(Read(), new JsonObject { ["threads"] = new JsonArray() });
            Assert.True(await launching.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        public void StopAt(int line = 1, string reason = "pause")
        {
            Write(from, new JsonObject { ["type"] = "event", ["event"] = "stopped", ["body"] = new JsonObject { ["reason"] = reason, ["threadId"] = 41 } });
            Answer(Read(), new JsonObject { ["stackFrames"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "main", ["line"] = line }) });
            Assert.True(WaitFor(() => Session.TryTake(out var next) && next.Kind == DebugEventKind.Stopped));
        }
        public void Dispose() { from.Dispose(); Session.Dispose(); input.Dispose(); output.Dispose(); to.Dispose(); }
    }

    [Fact]
    public async Task GotoUsesOfferedExactLineTargetsAndPreservesPauseOnNativeRejection()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch();
        adapter.StopAt();
        var session = adapter.Session;
        var asking = session.PrepareGoto("main.py", 4);
        var query = adapter.Read();
        Assert.Equal("gotoTargets", query["command"]!.GetValue<string>());
        Assert.Equal("main.py", query["arguments"]!["source"]!["path"]!.GetValue<string>());
        Assert.Equal(5, query["arguments"]!["line"]!.GetValue<int>());
        adapter.Answer(query, GotoAdapter.Targets((7, 5), (8, 5), (9, 6), (-1, 5)));
        var found = Assert.IsType<DebugGotoTargets>(await asking);
        Assert.Equal(new[] { 7, 8 }, found.Targets.Select(item => item.Id));
        Assert.All(found.Targets, target => { Assert.Equal(4, target.Line); Assert.Equal(2, target.Column); });
        Assert.False(await session.Goto(found, 999));
        var moving = session.Goto(found, 8);
        var move = adapter.Read();
        Assert.Equal("goto", move["command"]!.GetValue<string>());
        Assert.Equal(41, move["arguments"]!["threadId"]!.GetValue<int>());
        Assert.Equal(8, move["arguments"]!["targetId"]!.GetValue<int>());
        adapter.Fail(move);
        await Assert.ThrowsAnyAsync<Exception>(() => moving);
        Assert.Equal(DebugState.Stopped, session.State);
        Assert.False(await session.Goto(found, 7));
        var retry = session.PrepareGoto("main.py", 4);
        adapter.Answer(adapter.Read(), GotoAdapter.Targets((10, 5)));
        var current = Assert.IsType<DebugGotoTargets>(await retry);
        var accepted = session.Goto(current, 10);
        adapter.Answer(adapter.Read());
        Assert.True(await accepted);
        Assert.False(await session.Goto(current, 10));
        adapter.StopAt(5, "goto");
        Assert.Equal(DebugState.Stopped, session.State);
    }

    [Fact]
    public async Task GotoTargetsCannotSurviveResumeOrASecondStop()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch();
        adapter.StopAt();
        var session = adapter.Session;
        var first = session.PrepareGoto("main.py", 4);
        adapter.Answer(adapter.Read(), GotoAdapter.Targets((1, 5)));
        var ready = Assert.IsType<DebugGotoTargets>(await first);
        session.StepOver();
        var step = adapter.Read();
        Assert.Equal("next", step["command"]!.GetValue<string>());
        adapter.Answer(step);
        Assert.False(await session.Goto(ready, 1));
        Assert.Null(await session.PrepareGoto("main.py", 4));
        adapter.StopAt(2);
        Assert.False(await session.Goto(ready, 1));
        var delayed = session.PrepareGoto("main.py", 4);
        var query = adapter.Read();
        session.StepOver();
        adapter.Answer(adapter.Read());
        adapter.StopAt(3);
        adapter.Answer(query, GotoAdapter.Targets((2, 5)));
        Assert.Null(await delayed);
    }

    [Fact]
    public async Task GotoRejectsOlderTargetQueriesAndThreadChanges()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch();
        adapter.StopAt();
        var session = adapter.Session;
        var older = session.PrepareGoto("main.py", 3);
        var oldRequest = adapter.Read();
        var newer = session.PrepareGoto("main.py", 4);
        adapter.Answer(adapter.Read(), GotoAdapter.Targets((2, 5)));
        var current = Assert.IsType<DebugGotoTargets>(await newer);
        adapter.Answer(oldRequest, GotoAdapter.Targets((1, 4)));
        Assert.Null(await older);
        var selecting = session.SelectThread(42);
        adapter.Answer(adapter.Read(), new JsonObject { ["stackFrames"] = new JsonArray() });
        Assert.NotNull(await selecting);
        Assert.False(await session.Goto(current, 2));
        using var other = new GotoAdapter();
        Assert.False(await other.Session.Goto(current, 2));
    }

    [Fact]
    public async Task GotoIsUnavailableWithoutTheNativeCapability()
    {
        using var adapter = new GotoAdapter();
        await adapter.Launch(capable: false);
        adapter.StopAt();
        Assert.False(adapter.Session.CanGoto);
        Assert.Null(await adapter.Session.PrepareGoto("main.py", 0));
        adapter.Session.StepOver();
        Assert.Equal("next", adapter.Read()["command"]!.GetValue<string>());
    }
}
