using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Horizon.Tests;

/// <summary>
/// The debug adapter client, against an adapter played by hand over a pipe (always), and against netcoredbg on a little
/// program built for it (when netcoredbg is on the machine, next to Hex's exe, on the PATH or where HEX_NETCOREDBG says).
/// </summary>
public partial class DebugSessionTests
{
    private static bool WaitFor(Func<bool> until, int milliseconds = 20000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            if (until())
                return true;
            Thread.Sleep(20);
        }

        return until();
    }

    // A message the way the adapter writes one
    private static void Write(Stream to, JsonObject message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        to.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        to.Write(body);
        to.Flush();
    }

    // The next request the session sent, or a failure saying so when none comes. On Linux and macOS runners a request
    // never came and the read sat there until the whole run was given up on three minutes later, saying nothing of where
    private static JsonObject ReadRequest(Stream from)
    {
        Task<JsonObject> reading = Task.Run(() => ReadRequestNow(from));
        if (!reading.Wait(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("the session sent no request for 15 seconds");
        return reading.Result;
    }

    private static JsonObject ReadRequestNow(Stream from)
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
    }

    private static void Answer(Stream to, JsonObject request, JsonObject? body = null) =>
        Write(to, new JsonObject { ["seq"] = 0, ["type"] = "response", ["request_seq"] = request["seq"]!.GetValue<int>(), ["command"] = request["command"]!.GetValue<string>(), ["success"] = true, ["body"] = body });

    [Fact]
    public void NativeCSharpPauseWorksBeforeAnyBreakpointAndAfterContinuing()
    {
        if (Netcoredbg() is not {} debugger) return;
        string folder = Path.Combine(Path.GetTempPath(), "hex pause " + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "pause.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"pause-ready\");\nwhile (true) { System.Threading.Thread.Sleep(10); }\n");
            using var build = Process.Start(new ProcessStartInfo("dotnet", ["build", "-o", "out", "-v", "q"]) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            string output = build.StandardOutput.ReadToEnd();
            build.WaitForExit();
            Assert.True(build.ExitCode == 0, output);
            using var session = DebugSession.Start(debugger, ["--interpreter=vscode"])!;
            Assert.True(session.Launch(Path.Combine(folder, "out", "pause.dll"), [], folder, new Dictionary<string, IReadOnlyCollection<int>>()).GetAwaiter().GetResult(), session.Failed);
            Assert.True(WaitFor(() => session.TryTake(out var next) && next.Kind == DebugEventKind.Output && next.Text.Contains("pause-ready"), 30000));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                session.Pause();
                DebugEvent stopped = default;
                Assert.True(WaitFor(() => session.TryTake(out stopped) && stopped.Kind == DebugEventKind.Stopped, 15000), "Pause produced no stopped event");
                Assert.Equal(DebugState.Stopped, session.State);
                Assert.NotEmpty(stopped.Frames);
                session.Continue();
                Assert.Equal(DebugState.Running, session.State);
            }
            session.Stop();
            Assert.True(WaitFor(() => session.State == DebugState.Ended, 10000));
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task ThreadSelectionRejectsOutOfOrderStacksAndAnswersAfterResume()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);
        var launching = session.Launch("fixture", [], Path.GetTempPath(), new Dictionary<string, IReadOnlyCollection<int>>());
        Answer(fromAdapter, ReadRequest(adapterIn));
        var launch = ReadRequest(adapterIn);
        Write(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "initialized" });
        Answer(fromAdapter, ReadRequest(adapterIn));
        Answer(fromAdapter, launch);
        Assert.True(await launching.WaitAsync(TimeSpan.FromSeconds(5)));
        JsonObject Stack(int id) => new() { ["stackFrames"] = new JsonArray(new JsonObject { ["id"] = id, ["name"] = "Frame" + id, ["line"] = 1 }) };
        Write(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "stopped", ["body"] = new JsonObject { ["reason"] = "pause", ["threadId"] = 1 } });
        Answer(fromAdapter, ReadRequest(adapterIn), Stack(1));
        Assert.True(WaitFor(() => session.State == DebugState.Stopped));
        var threads = session.Threads();
        Assert.Equal("threads", ReadAndAnswerThreads());
        Assert.Equal("Worker", Assert.Single(await threads).Name);
        string ReadAndAnswerThreads()
        {
            var request = ReadRequest(adapterIn);
            Answer(fromAdapter, request, new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 2, ["name"] = "Worker" }) });
            return request["command"]!.GetValue<string>();
        }
        var older = session.SelectThread(2);
        var oldRequest = ReadRequest(adapterIn);
        Assert.Equal(2, oldRequest["arguments"]!["threadId"]!.GetValue<int>());
        var newer = session.SelectThread(3);
        var newRequest = ReadRequest(adapterIn);
        Answer(fromAdapter, newRequest, Stack(3));
        Assert.Equal(3, Assert.Single((await newer)!).Id);
        Answer(fromAdapter, oldRequest, Stack(2));
        Assert.Null(await older);
        Assert.Equal(3, session.SelectedThread);
        var preparing = session.PrepareThreadSelection(2);
        Answer(fromAdapter, ReadRequest(adapterIn), Stack(2));
        var prepared = await preparing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(prepared);
        Assert.Equal(2, Assert.Single(prepared.Frames).Id);
        Assert.Equal(3, session.SelectedThread); // A UI that discards the response must leave stepping unchanged.
        var late = session.SelectThread(2);
        var lateRequest = ReadRequest(adapterIn);
        Assert.False(session.TrySelectThread(prepared)); // A newer selection supersedes the prepared stack.
        var beforeResume = session.PrepareThreadSelection(4);
        Answer(fromAdapter, ReadRequest(adapterIn), Stack(4));
        var readyBeforeResume = await beforeResume.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(readyBeforeResume);
        session.StepOver();
        var step = ReadRequest(adapterIn);
        Assert.Equal("next", step["command"]!.GetValue<string>());
        Assert.Equal(3, step["arguments"]!["threadId"]!.GetValue<int>());
        Assert.False(session.TrySelectThread(readyBeforeResume));
        Answer(fromAdapter, step);
        Answer(fromAdapter, lateRequest, Stack(2));
        Assert.Null(await late);
        Assert.Equal(3, session.SelectedThread);
        Assert.Equal(DebugState.Running, session.State);
        fromAdapter.Dispose();
    }

    [Theory]
    [InlineData("readOnly", true)]
    [InlineData("constant", true)]
    [InlineData("static", false)]
    [InlineData(null, false)]
    public async Task VariableAndEvaluationPreserveNativeReadOnlyHints(string? attribute, bool expected)
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);
        JsonObject Value() => new() { ["name"] = "answer", ["value"] = "42", ["result"] = "42", ["variablesReference"] = 2, ["evaluateName"] = "items[0]",
            ["presentationHint"] = attribute is null ? null : new JsonObject { ["attributes"] = new JsonArray(JsonValue.Create(attribute)) } };
        var variables = session.Variables(1);
        Answer(fromAdapter, ReadRequest(adapterIn), new JsonObject { ["variables"] = new JsonArray(Value()) });
        var variable = Assert.Single(await variables.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(expected, variable.ReadOnly);
        Assert.Equal("items[0]", variable.EvaluateName);
        var evaluation = session.EvaluateSaying("answer", 1);
        Answer(fromAdapter, ReadRequest(adapterIn), Value());
        Assert.Equal(expected, (await evaluation.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Value.ReadOnly);
        fromAdapter.Dispose();
    }

    [Theory]
    [InlineData("coreclr", false)]
    [InlineData("lldb-dap", false)]
    [InlineData("lldb-dap", true)]
    public void ExceptionFiltersAreConfiguredBeforeLaunchAndCanBeChanged(string kind, bool functions)
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);
        string selected = kind == "lldb-dap" ? "cpp_throw" : "raised";
        bool native = OperatingSystem.IsWindows() && kind == "lldb-dap" && functions;
        session.BreakOnException = filter => filter.Id == selected;
        var launching = session.Launch("fixture", [], Path.GetTempPath(), new Dictionary<string, IReadOnlyCollection<int>>(), kind);
        var initialize = ReadRequest(adapterIn);
        Answer(fromAdapter, initialize, new JsonObject { ["supportsFunctionBreakpoints"] = functions, ["exceptionBreakpointFilters"] = new JsonArray(
            new JsonObject { ["filter"] = selected, ["label"] = "Raised exceptions", ["default"] = false },
            new JsonObject { ["filter"] = "uncaught", ["label"] = "Uncaught exceptions", ["default"] = true }) });
        var launch = ReadRequest(adapterIn);
        Write(fromAdapter, new JsonObject { ["seq"] = 0, ["type"] = "event", ["event"] = "initialized" });
        var filters = ReadRequest(adapterIn);
        Assert.Equal("setExceptionBreakpoints", filters["command"]!.GetValue<string>());
        Assert.Equal(selected, Assert.Single(filters["arguments"]!["filters"]!.AsArray())!.GetValue<string>());
        Answer(fromAdapter, filters);
        if (native)
        {
            var runtime = ReadRequest(adapterIn);
            Assert.Equal("setFunctionBreakpoints", runtime["command"]!.GetValue<string>());
            Assert.Equal("_CxxThrowException", Assert.Single(runtime["arguments"]!["breakpoints"]!.AsArray())!["name"]!.GetValue<string>());
            Answer(fromAdapter, runtime);
        }
        var done = ReadRequest(adapterIn);
        Assert.Equal("configurationDone", done["command"]!.GetValue<string>());
        Answer(fromAdapter, done);
        if (kind == "lldb-dap")
        {
            var baseline = ReadRequest(adapterIn);
            Assert.Equal("threads", baseline["command"]!.GetValue<string>());
            var currentThreads = session.Threads();
            Assert.False(currentThreads.IsCompleted);
            Answer(fromAdapter, baseline, new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "Startup" }) });
            var currentRequest = ReadRequest(adapterIn);
            Assert.Equal("threads", currentRequest["command"]!.GetValue<string>());
            Answer(fromAdapter, currentRequest, new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 2, ["name"] = "Current" }) });
            Assert.True(currentThreads.Wait(5000));
            Assert.Equal("Current", Assert.Single(currentThreads.Result).Name);
        }
        Answer(fromAdapter, launch);
        Assert.True(launching.Wait(5000) && launching.Result);
        Assert.True(session.BreaksOnException(selected));
        Assert.True(session.ExceptionFilters.Single(filter => filter.Id == "uncaught").Default);
        if (native)
        {
            var adding = session.SetFunctionBreakpoints(["User.Target"]);
            var functionsRequest = ReadRequest(adapterIn);
            Assert.Equal(new[] { "User.Target", "_CxxThrowException" }, functionsRequest["arguments"]!["breakpoints"]!.AsArray().Select(point => point!["name"]!.GetValue<string>()));
            Answer(fromAdapter, functionsRequest);
            Assert.True(adding.Wait(5000));
        }
        var disabling = session.SetExceptionBreakpoints([]);
        var empty = ReadRequest(adapterIn);
        Assert.Empty(empty["arguments"]!["filters"]!.AsArray());
        Answer(fromAdapter, empty);
        if (native)
        {
            var runtime = ReadRequest(adapterIn);
            Assert.Equal("setFunctionBreakpoints", runtime["command"]!.GetValue<string>());
            Assert.Equal("User.Target", Assert.Single(runtime["arguments"]!["breakpoints"]!.AsArray())!["name"]!.GetValue<string>());
            Answer(fromAdapter, runtime);
        }
        Assert.True(disabling.Wait(5000));
        Assert.False(session.BreaksOnException(selected));
        // Let the session's background reader reach EOF before its stream is disposed on Unix.
        fromAdapter.Dispose();
    }

    [Fact]
    public async Task RunToAnUnverifiedLineRestoresConditionsWithoutContinuing()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);
        using var session = DebugSession.Over(sessionIn, toAdapter);
        string file = Path.Combine(Path.GetTempPath(), "RunTo.cs");
        var launching = session.Launch("fixture", [], Path.GetTempPath(), new Dictionary<string, IReadOnlyCollection<DebugBreakpoint>> { [file] = [new(4, "false")] });
        Answer(fromAdapter, ReadRequest(adapterIn), new JsonObject { ["supportsConditionalBreakpoints"] = true });
        var launch = ReadRequest(adapterIn);
        Write(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "initialized" });
        Answer(fromAdapter, ReadRequest(adapterIn));
        Answer(fromAdapter, ReadRequest(adapterIn));
        Answer(fromAdapter, launch);
        Assert.True(await launching.WaitAsync(TimeSpan.FromSeconds(5)));
        Write(fromAdapter, new JsonObject { ["type"] = "event", ["event"] = "stopped", ["body"] = new JsonObject { ["reason"] = "pause", ["threadId"] = 1 } });
        Answer(fromAdapter, ReadRequest(adapterIn), new JsonObject { ["stackFrames"] = new JsonArray() });
        var pausedThreads = ReadRequest(adapterIn);
        Assert.Equal("threads", pausedThreads["command"]!.GetValue<string>());
        Answer(fromAdapter, pausedThreads, new JsonObject { ["threads"] = new JsonArray() });
        Assert.True(WaitFor(() => session.State == DebugState.Stopped));
        Task running = session.RunTo(file, 4);
        var temporary = ReadRequest(adapterIn);
        Assert.Equal("setBreakpoints", temporary["command"]!.GetValue<string>());
        Assert.Null(temporary["arguments"]!["breakpoints"]![0]!["condition"]);
        session.StepOver();
        Assert.Equal(DebugState.Stopped, session.State);
        Answer(fromAdapter, temporary, new JsonObject { ["breakpoints"] = new JsonArray(new JsonObject { ["verified"] = false, ["line"] = 5 }) });
        var restored = ReadRequest(adapterIn);
        Assert.Equal("setBreakpoints", restored["command"]!.GetValue<string>());
        Assert.Equal("false", restored["arguments"]!["breakpoints"]![0]!["condition"]!.GetValue<string>());
        Answer(fromAdapter, restored);
        await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DebugState.Stopped, session.State);
        fromAdapter.Dispose();
    }

    [Fact]
    public void A_launch_sends_the_breakpoints_and_a_stop_comes_back_with_where()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);

        using var session = DebugSession.Over(sessionIn, toAdapter);
        var breakpoints = new Dictionary<string, IReadOnlyCollection<int>> { [@"C:\app\Program.cs"] = [4] };
        Task<bool> launching = session.Launch(@"C:\app\bin\app.dll", [], @"C:\app", breakpoints);

        // The adapter's side of it, in the order the protocol goes
        JsonObject initialize = ReadRequest(adapterIn);
        Assert.Equal("initialize", initialize["command"]!.GetValue<string>());
        Answer(fromAdapter, initialize);
        JsonObject launch = ReadRequest(adapterIn);
        Assert.Equal("launch", launch["command"]!.GetValue<string>());
        Assert.Equal(@"C:\app\bin\app.dll", launch["arguments"]!["program"]!.GetValue<string>());
        Write(fromAdapter, new JsonObject { ["seq"] = 0, ["type"] = "event", ["event"] = "initialized" });

        JsonObject set = ReadRequest(adapterIn);
        Assert.Equal("setBreakpoints", set["command"]!.GetValue<string>());
        Assert.Equal(5, set["arguments"]!["breakpoints"]![0]!["line"]!.GetValue<int>());
        Answer(fromAdapter, set);
        JsonObject done = ReadRequest(adapterIn);
        Assert.Equal("configurationDone", done["command"]!.GetValue<string>());
        Answer(fromAdapter, done);
        Answer(fromAdapter, launch);
        Assert.True(launching.Wait(5000) && launching.Result);
        Assert.Equal(DebugState.Running, session.State);

        // Stopped at the breakpoint, the session asks where and then says so
        Write(fromAdapter, new JsonObject { ["seq"] = 0, ["type"] = "event", ["event"] = "stopped", ["body"] = new JsonObject { ["reason"] = "breakpoint", ["threadId"] = 7 } });
        JsonObject trace = ReadRequest(adapterIn);
        Assert.Equal("stackTrace", trace["command"]!.GetValue<string>());
        Assert.Equal(7, trace["arguments"]!["threadId"]!.GetValue<int>());
        Answer(fromAdapter, trace, new JsonObject
        {
            ["stackFrames"] = new JsonArray(new JsonObject { ["id"] = 1000, ["name"] = "Program.Main()", ["line"] = 5, ["column"] = 9, ["source"] = new JsonObject { ["path"] = @"C:\app\Program.cs" } })
        });

        DebugEvent stopped = default;
        Assert.True(WaitFor(() => session.TryTake(out stopped)));
        Assert.Equal(DebugEventKind.Stopped, stopped.Kind);
        Assert.Equal(DebugState.Stopped, session.State);
        Assert.Equal((@"C:\app\Program.cs", 4, 8), (stopped.Frames[0].File, stopped.Frames[0].Line, stopped.Frames[0].Column));

        // A step goes on the thread that stopped
        session.StepOver();
        Assert.Equal(DebugState.Running, session.State);
        JsonObject next = ReadRequest(adapterIn);
        Assert.Equal("next", next["command"]!.GetValue<string>());
        Assert.Equal(7, next["arguments"]!["threadId"]!.GetValue<int>());

        Write(fromAdapter, new JsonObject { ["seq"] = 0, ["type"] = "event", ["event"] = "exited", ["body"] = new JsonObject { ["exitCode"] = 3 } });
        Assert.True(WaitFor(() => session.State == DebugState.Ended));

        // The adapter's end closed first, the session's reading thread gets to the end of the pipe and lets go of it. On
        // Linux and macOS letting go of a pipe another thread is reading waits for that read, and the usings let go of
        // the session's end first, so the run hung there for good
        fromAdapter.Dispose();
    }

    [Fact]
    public void An_attach_names_the_process_and_an_evaluation_that_failed_says_what_the_adapter_said()
    {
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.In, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var sessionIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);

        using var session = DebugSession.Over(sessionIn, toAdapter);
        var breakpoints = new Dictionary<string, IReadOnlyCollection<int>> { [@"C:\app\Tests.cs"] = [9] };
        Task<bool> attaching = session.Attach(4321, breakpoints);

        JsonObject initialize = ReadRequest(adapterIn);
        Assert.Equal("initialize", initialize["command"]!.GetValue<string>());
        Answer(fromAdapter, initialize);
        JsonObject attach = ReadRequest(adapterIn);
        Assert.Equal("attach", attach["command"]!.GetValue<string>());
        Assert.Equal(4321, attach["arguments"]!["processId"]!.GetValue<int>());
        Write(fromAdapter, new JsonObject { ["seq"] = 0, ["type"] = "event", ["event"] = "initialized" });

        JsonObject set = ReadRequest(adapterIn);
        Assert.Equal(10, set["arguments"]!["breakpoints"]![0]!["line"]!.GetValue<int>());
        Answer(fromAdapter, set);
        JsonObject done = ReadRequest(adapterIn);
        Assert.Equal("configurationDone", done["command"]!.GetValue<string>());
        Answer(fromAdapter, done);
        Answer(fromAdapter, attach);
        Assert.True(attaching.Wait(5000) && attaching.Result);
        Assert.Equal(DebugState.Running, session.State);

        // The adapter's message for an expression it couldn't evaluate comes back as it said it
        Task<DebugEvaluation> evaluating = session.EvaluateSaying("args.Length", 0);
        JsonObject evaluate = ReadRequest(adapterIn);
        Assert.Equal("evaluate", evaluate["command"]!.GetValue<string>());
        Write(fromAdapter, new JsonObject
        {
            ["seq"] = 0, ["type"] = "response", ["request_seq"] = evaluate["seq"]!.GetValue<int>(), ["command"] = "evaluate", ["success"] = false,
            ["message"] = "error: The name 'args.Length' does not exist in the current context"
        });
        Assert.True(evaluating.Wait(5000));
        Assert.Null(evaluating.Result.Value);
        Assert.Equal("error: The name 'args.Length' does not exist in the current context", evaluating.Result.Error);

        fromAdapter.Dispose();
    }

    // netcoredbg, wherever it is on this machine, null when it isn't
    private static string? Netcoredbg()
    {
        if (Environment.GetEnvironmentVariable("HEX_NETCOREDBG") is { Length: > 0 } said && File.Exists(said))
            return said;

        string beside = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "hex", "Hex", "bin", "Debug", "net11.0", "netcoredbg", "netcoredbg.exe"));
        if (File.Exists(beside))
            return beside;

        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string there = Path.Combine(folder.Trim('"'), OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg");
            if (File.Exists(there))
                return there;
        }

        return null;
    }

    [Fact]
    public void Netcoredbg_stops_a_real_program_at_its_breakpoint_and_says_its_variables()
    {
        if (Netcoredbg() is not { } debugger)
            return;

        string folder = Path.Combine(Path.GetTempPath(), "horizon-debug-test-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "debugged.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            string source = Path.Combine(folder, "Program.cs");
            File.WriteAllText(source, "int answer = 6 * 7;\nstring said = \"forty two\";\nSystem.Console.WriteLine(said);\nSystem.Console.WriteLine(answer);\n");

            var build = Process.Start(new ProcessStartInfo("dotnet", ["build", "-c", "Debug", "-o", Path.Combine(folder, "out"), "-nologo", "-v", "q"])
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            build.StandardOutput.ReadToEnd();
            build.WaitForExit();
            Assert.Equal(0, build.ExitCode);

            using DebugSession session = DebugSession.Start(debugger, ["--interpreter=vscode"])!;
            Assert.NotNull(session);
            var breakpoints = new Dictionary<string, IReadOnlyCollection<int>> { [source] = [2] };
            Task<bool> launched = session.Launch(Path.Combine(folder, "out", "debugged.dll"), [], folder, breakpoints);
            Assert.True(launched.Wait(30000), "it never launched");
            Assert.True(launched.Result, session.Failed);

            DebugEvent stopped = default;
            Assert.True(WaitFor(() => session.TryTake(out stopped) && stopped.Kind == DebugEventKind.Stopped, 30000), "it never stopped");
            Assert.Equal(2, stopped.Frames[0].Line);
            Assert.Equal(source, stopped.Frames[0].File, ignoreCase: true);

            IReadOnlyList<DebugScope> scopes = session.Scopes(stopped.Frames[0].Id).Result;
            IReadOnlyList<DebugVariable> locals = session.Variables(scopes[0].Reference).Result;
            Assert.Contains(locals, variable => variable.Name == "answer" && variable.Value == "42");
            Assert.Equal("42", session.Evaluate("answer", stopped.Frames[0].Id).Result);

            session.Continue();
            var printed = new StringBuilder();
            Assert.True(WaitFor(() =>
            {
                while (session.TryTake(out DebugEvent next))
                {
                    if (next.Kind == DebugEventKind.Output)
                        printed.Append(next.Text);
                    if (next.Kind == DebugEventKind.Ended)
                        return true;
                }

                return false;
            }, 30000), "it never ended");
            Assert.Contains("forty two", printed.ToString());
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
