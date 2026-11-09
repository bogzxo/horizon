using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests;

/// <summary>
/// A debug session's breakpoints beyond their lines against an adapter played by hand that does conditions and nothing
/// else (netcoredbg's way), the hit counts and the messages to log done on this side. And a line of the program's that
/// isn't UTF-8, which ended the whole session as if the program had.
/// </summary>
public class DebugSessionBreakpointTests
{
    private static bool WaitFor(Func<bool> until, int milliseconds = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            if (until())
                return true;
            Thread.Sleep(10);
        }

        return until();
    }

    private static void Write(Stream to, JsonObject message) => WriteBytes(to, Encoding.UTF8.GetBytes(message.ToJsonString()));

    private static void WriteBytes(Stream to, byte[] body)
    {
        to.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        to.Write(body);
        to.Flush();
    }

    private static JsonObject ReadRequest(Stream from)
    {
        Task<JsonObject> reading = Task.Run(() =>
        {
            var header = new StringBuilder();
            int length = -1;
            while (true)
            {
                int b = from.ReadByte();
                if (b < 0)
                    throw new EndOfStreamException("the session closed its end of the pipe");
                if (b == '\n')
                {
                    string line = header.ToString().TrimEnd('\r');
                    header.Clear();
                    if (line.Length == 0 && length >= 0) break;
                    if (line.StartsWith("Content-Length:")) length = int.Parse(line[15..].Trim());
                    continue;
                }
                header.Append((char)b);
            }

            byte[] body = new byte[length];
            from.ReadExactly(body);
            return JsonNode.Parse(body)!.AsObject();
        });
        if (!reading.Wait(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("the session sent no request for 15 seconds");
        return reading.Result;
    }

    private static void Answer(Stream to, JsonObject request, JsonObject? body = null) =>
        Write(to, new JsonObject { ["seq"] = 0, ["type"] = "response", ["request_seq"] = request["seq"]!.GetValue<int>(), ["command"] = request["command"]!.GetValue<string>(), ["success"] = true, ["body"] = body });

    private static JsonObject Event(string name, JsonObject? body = null) => new() { ["seq"] = 0, ["type"] = "event", ["event"] = name, ["body"] = body };

    // A session launched with these breakpoints of Program.cs, the adapter saying it does conditions only
    private static void Launched(DebugSession session, Stream adapterIn, Stream fromAdapter, params DebugBreakpoint[] set)
    {
        var breakpoints = new Dictionary<string, IReadOnlyCollection<DebugBreakpoint>> { [@"C:\app\Program.cs"] = set };
        Task<bool> launching = session.Launch(@"C:\app\bin\app.dll", [], @"C:\app", breakpoints);

        JsonObject initialize = ReadRequest(adapterIn);
        Answer(fromAdapter, initialize, new JsonObject { ["supportsConditionalBreakpoints"] = true, ["supportsExceptionInfoRequest"] = true });
        JsonObject launch = ReadRequest(adapterIn);
        Write(fromAdapter, Event("initialized"));

        JsonObject setting = ReadRequest(adapterIn);
        Assert.Equal("setBreakpoints", setting["command"]!.GetValue<string>());
        JsonArray asked = setting["arguments"]!["breakpoints"]!.AsArray();
        for (int i = 0; i < set.Length; i++)
        {
            // The condition goes to the adapter, which does them, the rest stays here. With an editor's own evaluation
            // the condition stays here too
            Assert.Equal(session.Evaluates is null ? set[i].Condition : null, asked[i]!["condition"]?.GetValue<string>());
            Assert.Null(asked[i]!["hitCondition"]);
            Assert.Null(asked[i]!["logMessage"]);
        }
        Answer(fromAdapter, setting, new JsonObject { ["breakpoints"] = new JsonArray(set.Select((one, i) => (JsonNode)new JsonObject { ["id"] = i + 1, ["verified"] = true, ["line"] = one.Line + 1 }).ToArray()) });
        JsonObject done = ReadRequest(adapterIn);
        Answer(fromAdapter, done);
        Answer(fromAdapter, launch);
        Assert.True(launching.Wait(5000) && launching.Result);
    }

    // A stop at a line of Program.cs, the stack it's asked for answered
    private static void StopAt(Stream adapterIn, Stream fromAdapter, int line)
    {
        Write(fromAdapter, Event("stopped", new JsonObject { ["reason"] = "breakpoint", ["threadId"] = 7 }));
        JsonObject trace = ReadRequest(adapterIn);
        Assert.Equal("stackTrace", trace["command"]!.GetValue<string>());
        Answer(fromAdapter, trace, new JsonObject
        {
            ["stackFrames"] = new JsonArray(new JsonObject { ["id"] = 1000, ["name"] = "Program.Main()", ["line"] = line + 1, ["column"] = 9, ["source"] = new JsonObject { ["path"] = @"C:\app\Program.cs" } })
        });
    }

    [Fact]
    public void A_hit_count_goes_on_from_the_hits_it_doesnt_stop_on()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);
        Launched(session, adapterIn, fromAdapter, new DebugBreakpoint(4, Condition: "i > 1", Hits: new DebugHitCondition(DebugHitKind.Equal, 3)));

        // The first two hits are gone on from without a word
        for (int hit = 1; hit <= 2; hit++)
        {
            StopAt(adapterIn, fromAdapter, 4);
            JsonObject going = ReadRequest(adapterIn);
            Assert.Equal("continue", going["command"]!.GetValue<string>());
            Answer(fromAdapter, going);
        }

        Assert.False(session.TryTake(out _));
        Assert.Equal(DebugState.Running, session.State);

        // The third it stops on
        StopAt(adapterIn, fromAdapter, 4);
        DebugEvent stopped = default;
        Assert.True(WaitFor(() => session.TryTake(out stopped)));
        Assert.Equal(DebugEventKind.Stopped, stopped.Kind);
        Assert.Equal(3, session.HitsOf(@"C:\app\Program.cs", 4));
        fromAdapter.Dispose();
    }

    [Fact]
    public void A_condition_an_editor_evaluates_is_checked_here_and_one_that_cant_be_stops_with_why()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);

        // What the editor's evaluation says each hit, false, then an error, then something that isn't a bool
        var said = new Queue<DebugEvaluation>([
            new DebugEvaluation(new DebugVariable("c", "false", "bool", 0), null),
            new DebugEvaluation(null, "args isn't here"),
            new DebugEvaluation(new DebugVariable("c", "5", "int", 0), null)
        ]);
        session.Evaluates = (_, _) => Task.FromResult(said.Dequeue());
        Launched(session, adapterIn, fromAdapter, new DebugBreakpoint(4, Condition: "args.Length == 0"));

        // False it's gone on from without a word
        StopAt(adapterIn, fromAdapter, 4);
        JsonObject going = ReadRequest(adapterIn);
        Assert.Equal("continue", going["command"]!.GetValue<string>());
        Answer(fromAdapter, going);
        Assert.False(session.TryTake(out _));

        // Not evaluated it stops, with why
        StopAt(adapterIn, fromAdapter, 4);
        DebugEvent stopped = default;
        Assert.True(WaitFor(() => session.TryTake(out stopped)));
        Assert.Equal(DebugEventKind.Stopped, stopped.Kind);
        Assert.Equal(new DebugConditionFailure(@"C:\app\Program.cs", 4, "args.Length == 0", "args isn't here"), stopped.ConditionFailed);

        session.Continue();
        Assert.True(WaitFor(() => session.TryTake(out _)));
        JsonObject again = ReadRequest(adapterIn);
        Answer(fromAdapter, again);

        // Something that isn't true or false stops it as well, with what it came to
        StopAt(adapterIn, fromAdapter, 4);
        Assert.True(WaitFor(() => session.TryTake(out stopped)));
        Assert.Equal("5", stopped.ConditionFailed?.CameTo);
        fromAdapter.Dispose();
    }

    [Fact]
    public void A_message_to_log_is_said_with_its_expressions_and_it_goes_on()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);
        Launched(session, adapterIn, fromAdapter, new DebugBreakpoint(4, LogMessage: "i is {i} in $FUNCTION, {{not}} {nope}"));

        StopAt(adapterIn, fromAdapter, 4);
        JsonObject first = ReadRequest(adapterIn);
        Assert.Equal(("evaluate", "i"), (first["command"]!.GetValue<string>(), first["arguments"]!["expression"]!.GetValue<string>()));
        Answer(fromAdapter, first, new JsonObject { ["result"] = "4", ["type"] = "int", ["variablesReference"] = 0 });
        JsonObject second = ReadRequest(adapterIn);
        Write(fromAdapter, new JsonObject { ["seq"] = 0, ["type"] = "response", ["request_seq"] = second["seq"]!.GetValue<int>(), ["command"] = "evaluate", ["success"] = false, ["message"] = "no nope" });
        JsonObject going = ReadRequest(adapterIn);
        Assert.Equal("continue", going["command"]!.GetValue<string>());

        DebugEvent logged = default;
        Assert.True(WaitFor(() => session.TryTake(out logged)));
        Assert.Equal(DebugEventKind.Output, logged.Kind);
        Assert.Equal("console\ti is 4 in Program.Main(), {not} <nope: no nope>\n", logged.Text);
        fromAdapter.Dispose();
    }

    [Fact]
    public void A_line_of_output_that_isnt_UTF8_is_read_and_the_session_goes_on()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);
        Launched(session, adapterIn, fromAdapter);

        // What netcoredbg sent for Horizon's "Created window(<1600×900>)!", the × a lone 0xFF
        byte[] before = Encoding.UTF8.GetBytes("{\"seq\":78,\"event\":\"output\",\"type\":\"event\",\"body\":{\"category\":\"stdout\",\"output\":\"window(<1600");
        byte[] after = Encoding.UTF8.GetBytes(" 900>)!\\r\\n\"}}");
        WriteBytes(fromAdapter, [.. before, 0xFF, .. after]);
        Write(fromAdapter, Event("exited", new JsonObject { ["exitCode"] = 3 }));

        var seen = new List<DebugEvent>();
        Assert.True(WaitFor(() =>
        {
            while (session.TryTake(out DebugEvent next))
                seen.Add(next);
            return seen.Any(one => one.Kind == DebugEventKind.Ended);
        }));

        Assert.Contains(seen, one => one.Kind == DebugEventKind.Output && one.Text == "stdout\twindow(<1600\uFFFD 900>)!\r\n");
        DebugEvent ended = seen.Single(one => one.Kind == DebugEventKind.Ended);
        Assert.Equal((3, string.Empty), (ended.ExitCode, ended.Text));
        fromAdapter.Dispose();
    }

    [Fact]
    public void A_message_comes_apart_into_its_text_and_expressions()
    {
        Assert.Equal(
            [new DebugSession.LogPart("x is ", false), new DebugSession.LogPart("box.X", true), new DebugSession.LogPart(" {braces}", false)],
            DebugSession.LogParts("x is { box.X } {{braces}}"));
    }
}
