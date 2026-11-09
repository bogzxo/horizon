using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>Where a debugged program is, one call of its stack, the line and column counted from 0.</summary>
public readonly record struct DebugFrame(int Id, string Name, string? File, int Line, int Column)
{
    public string? InstructionPointerReference { get; init; }
    public DebugSource? Source { get; init; }
}

/// <summary>A scope of a frame (its locals, its arguments), whose variables are asked for by the reference.</summary>
public readonly record struct DebugScope(string Name, int Reference);

/// <summary>A variable and what it is, with a reference to ask for what's inside it when there is anything, 0 when not.</summary>
public readonly record struct DebugVariable(string Name, string Value, string? Type, int Reference)
{
    public bool ReadOnly { get; init; }
    public string? EvaluateName { get; init; }
    public string? MemoryReference { get; init; }
    public int? IndexedVariables { get; init; }
    public int? NamedVariables { get; init; }
}

/// <summary>What an expression came to, or what the debugger said when it couldn't evaluate it (its message, null when it said nothing).</summary>
public readonly record struct DebugEvaluation(DebugVariable? Value, string? Error);

/// <summary>
/// What happened in a debug session, taken on the program's own thread (<see cref="DebugSession.TryTake"/>). A stop on an
/// exception has what the exception was when the debugger could say. An end's text is empty when the debugger said it
/// ended and why it went otherwise (the debugger gone, the pipe to it broken). A stop at a breakpoint whose condition
/// couldn't be evaluated has why.
/// </summary>
public readonly record struct DebugEvent(DebugEventKind Kind, string Text, IReadOnlyList<DebugFrame> Frames, int ExitCode, DebugException? Exception = null,
    DebugConditionFailure? ConditionFailed = null);

/// <summary>An exception a program stopped on, its type's whole name, its message, and its stack as .NET writes one, null when the debugger had none.</summary>
public sealed record DebugException(string Type, string Message, string? StackTrace, bool Unhandled);

public enum DebugEventKind
{
    /// <summary>It stopped (a breakpoint, a step, a pause, an exception), <see cref="DebugEvent.Text"/> says why and the frames are where.</summary>
    Stopped,

    /// <summary>It's going again.</summary>
    Continued,

    /// <summary>Something it or the debugger wrote, the category (stdout, stderr, console) before a tab in the text.</summary>
    Output,

    /// <summary>It's over, with the program's exit code when it said one.</summary>
    Ended,

    /// <summary>Function breakpoint verification or placement changed.</summary>
    BreakpointsChanged
}

/// <summary>Where a debug session is.</summary>
public enum DebugState
{
    Starting,
    Running,
    Stopped,
    Ended
}

/// <summary>
/// A program run under a debugger that talks the Debug Adapter Protocol, netcoredbg for .NET (<c>netcoredbg
/// --interpreter=vscode</c>, Samsung's, MIT). It starts the adapter, launches the program through it with the breakpoints
/// it was given, and then goes where it's told (<see cref="Continue"/>, the steps, <see cref="Pause"/>, <see cref="Stop"/>).
/// When it stops it asks where, the stack of the thread that stopped, and what happened is queued for whoever runs the
/// UI to take on their own thread with <see cref="TryTake"/>, the way a language server's diagnostics are. Lines are the
/// editor's, counted from 0, the protocol's from 1 is this side's business.
/// <code>
/// var session = DebugSession.Start(netcoredbg, ["--interpreter=vscode"]);
/// session.Launch(dll, [], folder, breakpoints);
/// while (session.TryTake(out DebugEvent happened)) ...
/// </code>
/// </summary>
public sealed partial class DebugSession : IDisposable
{
    private readonly DebugAdapterConnection connection;
    private readonly Process? adapter;
    /// <summary>Whether ending this session also ends its program. False for an externally started process.</summary>
    public bool TerminatesDebuggeeOnStop { get; }
    /// <summary>Native user-code filtering, applied when the session launches or attaches.</summary>
    public bool JustMyCode { get; set; } = true;
    private readonly object stoppingLock = new();
    private Task? stopping;
    private readonly ConcurrentQueue<DebugEvent> happened = new();
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile DebugState state = DebugState.Starting;
    private int stoppedThread = -1;

    /// <summary>Where it is.</summary>
    public DebugState State => state;

    /// <summary>What went wrong starting it, null while nothing did.</summary>
    public string? Failed { get; private set; }

    private DebugSession(DebugAdapterConnection connection, Process? adapter, bool terminateDebuggeeOnStop = true)
    {
        this.connection = connection;
        this.adapter = adapter;
        TerminatesDebuggeeOnStop = terminateDebuggeeOnStop;
        connection.OnEvent = Happened;
        // The pipe to the debugger broken, said with what broke it. It used to end the session as if the program had
        connection.Closed = broke => End(-1, broke?.Message ?? LOST);
    }

    /// <summary>What an end says when the debugger went without saying the program ended, the end of its output.</summary>
    public const string LOST = "the debugger went away";

    /// <summary>Starts a debug adapter, null when it can't be started (not there, not a program).</summary>
    public static DebugSession? Start(string command, IReadOnlyList<string> arguments, bool terminateDebuggeeOnStop = true)
    {
        try
        {
            var start = new ProcessStartInfo(command)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);

            Process? process = Process.Start(start);
            if (process is null)
                return null;
            Horizon.Core.Platform.ChildProcesses.Adopt(process);

            // What it says on stderr is nobody's, but it's read so the pipe never fills and stops it
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return new DebugSession(new DebugAdapterConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream), process, terminateDebuggeeOnStop);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A session over streams an adapter is on the other end of, for a test.</summary>
    public static DebugSession Over(Stream input, Stream output, bool terminateDebuggeeOnStop = true) => new(new DebugAdapterConnection(input, output), null, terminateDebuggeeOnStop);

    /// <summary>The kind of debugger netcoredbg is, .NET's, the one <see cref="Launch"/> goes by when it isn't told.</summary>
    public const string CORECLR = "coreclr";
    public const string PYTHON = "debugpy";

    /// <summary>
    /// Launches a program under the debugger, with its breakpoints (file, then lines counted from 0), and comes back once
    /// it's going or didn't go (<see cref="Failed"/>). netcoredbg (coreclr) takes a .dll and runs it with dotnet, lldb-dap
    /// (lldb-dap) a program of the machine's own, a D one built with -g.
    /// </summary>
    /// <param name="kind">What kind of debugger it is, the type of its launch, coreclr or lldb-dap.</param>
    /// <param name="output">
    /// A file what the program writes goes to, for a debugger that doesn't say it (lldb-dap on Windows), null for the
    /// debugger to say it itself.
    /// </param>
    /// <param name="environment">What's put in the program's environment over what it would have anyway, null for nothing.</param>
    public Task<bool> Launch(string program, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, IReadOnlyCollection<int>> breakpointsByFile, string kind = CORECLR, string? output = null, IReadOnlyDictionary<string, string>? environment = null, string? interpreter = null, string? module = null) =>
        Launch(program, arguments, workingDirectory, Plain(breakpointsByFile), kind, output, environment, interpreter, module);

    /// <summary>The same with breakpoints that can have a condition, a hit count or a message to log each.</summary>
    /// <param name="environment">What's put in the program's environment over what it would have anyway, null for nothing.</param>
    public async Task<bool> Launch(string program, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, IReadOnlyCollection<DebugBreakpoint>> breakpointsByFile, string kind = CORECLR, string? output = null, IReadOnlyDictionary<string, string>? environment = null, string? interpreter = null, string? module = null)
    {
        try
        {
            await Initialize(kind).ConfigureAwait(false);

            var args = new JsonArray();
            foreach (string argument in arguments)
                args.Add((JsonNode?)JsonValue.Create(argument));

            JsonObject launch = kind is CORECLR or PYTHON
                ? new JsonObject
                {
                    ["name"] = "Hex",
                    ["type"] = kind,
                    ["request"] = "launch",
                    ["program"] = program,
                    ["args"] = args,
                    ["cwd"] = workingDirectory,
                    ["stopAtEntry"] = false,
                    ["console"] = "internalConsole",
                    ["justMyCode"] = JustMyCode
                }
                : new JsonObject
                {
                    ["name"] = "Hex",
                    ["type"] = kind,
                    ["request"] = "launch",
                    ["program"] = program,
                    ["args"] = args,
                    ["cwd"] = workingDirectory,
                    ["stopOnEntry"] = false
                };

            if (kind == PYTHON)
            {
                launch.Remove("stopAtEntry");
                if (module is not null) { launch.Remove("program"); launch["module"] = module; }
                launch["stopOnEntry"] = false;
                launch["redirectOutput"] = true;
                if (interpreter is not null) launch["python"] = new JsonArray(interpreter);
            }

            // netcoredbg and debugpy take variables as an object, lldb-dap as NAME=value lines
            if (environment is { Count: > 0 })
            {
                if (kind is CORECLR or PYTHON)
                {
                    var env = new JsonObject();
                    foreach (var (name, value) in environment)
                        env[name] = value;
                    launch["env"] = env;
                }
                else
                {
                    var env = new JsonArray();
                    foreach (var (name, value) in environment)
                        env.Add((JsonNode?)JsonValue.Create($"{name}={value}"));
                    launch["env"] = env;
                }
            }

            // What it writes into a file, stdin left as it is and stdout and stderr both there
            if (output is not null)
                launch["stdio"] = new JsonArray(null, output, output);
            await Configure(connection.Request("launch", launch), breakpointsByFile).ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            Failed = e.Message;
            End(-1);
            return false;
        }
    }

    /// <summary>
    /// The debugger put on a program that's running already, by its process id, with its breakpoints (file, then lines
    /// counted from 0), and back once it's on or couldn't get on (<see cref="Failed"/>). A test host started with
    /// VSTEST_HOST_DEBUG waits for this before it runs a test. Stop ends owned test hosts; create the session with
    /// terminateDebuggeeOnStop:false to leave an externally started program running when debugging ends.
    /// </summary>
    public Task<bool> Attach(int processId, IReadOnlyDictionary<string, IReadOnlyCollection<int>> breakpointsByFile, string kind = CORECLR, DateTime? expectedStartTime = null) =>
        Attach(processId, Plain(breakpointsByFile), kind, expectedStartTime);

    /// <summary>The same with breakpoints that can have a condition, a hit count or a message to log each.</summary>
    public async Task<bool> Attach(int processId, IReadOnlyDictionary<string, IReadOnlyCollection<DebugBreakpoint>> breakpointsByFile, string kind = CORECLR, DateTime? expectedStartTime = null)
    {
        try
        {
            await Initialize(kind).ConfigureAwait(false);
            // Adapter startup can take time. Revalidate a picker identity immediately before sending the PID.
            if (expectedStartTime is {} expected)
            {
                using var selected = Process.GetProcessById(processId);
                if (selected.HasExited || selected.StartTime.ToUniversalTime() != expected.ToUniversalTime())
                    throw new InvalidOperationException("The selected process has exited or been replaced.");
            }
            await Configure(connection.Request("attach", new JsonObject
            {
                ["name"] = "Hex",
                ["type"] = kind,
                ["request"] = "attach",
                ["processId"] = processId,
                ["justMyCode"] = JustMyCode
            }), breakpointsByFile).ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            Failed = e.Message;
            End(-1);
            return false;
        }
    }

    // The first thing said to an adapter, who's asking and how lines and paths are counted, and what it says it can do kept
    private async Task Initialize(string kind)
    {
        completionAdapter = kind;
        pausesProcess = kind == "lldb-dap";
        threadBaseline = kind == "lldb-dap" ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        windowsLldb = OperatingSystem.IsWindows() && kind == "lldb-dap";
        Capable(await connection.Request("initialize", new JsonObject
    {
        ["clientID"] = "hex",
        ["clientName"] = "Hex",
        ["adapterID"] = kind,
        ["linesStartAt1"] = true,
        ["columnsStartAt1"] = true,
        ["pathFormat"] = "path",
        ["supportsVariableType"] = true,
        ["supportsMemoryReferences"] = true,
        ["supportsVariablePaging"] = true
        }).ConfigureAwait(false));
    }

    /// <summary>
    /// A launch or an attach sent, answered once the configuration is done, which the adapter asks for with an
    /// initialized event once it's ready for the breakpoints.
    /// </summary>
    private async Task Configure(Task<JsonNode?> started, IReadOnlyDictionary<string, IReadOnlyCollection<DebugBreakpoint>> breakpointsByFile)
    {
        Task first = await Task.WhenAny(initialized.Task, started, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
        if (first == started)
            await started.ConfigureAwait(false);

        foreach (var (file, lines) in breakpointsByFile)
            await SetBreakpointsNow(file, lines).ConfigureAwait(false);
        await ConfigureExceptions().ConfigureAwait(false);
        if (InitialFunctionBreakpoints.Count > 0 && Can("supportsFunctionBreakpoints"))
            await SetFunctionBreakpoints(InitialFunctionBreakpoints).ConfigureAwait(false);
        await connection.Request("configurationDone").ConfigureAwait(false);
        if (threadBaseline is { } baseline)
        {
            // LLDB can return a cached startup list once, even if it has since stopped.
            // Drain it before any UI request for the current paused threads.
            try { await connection.Request("threads").ConfigureAwait(false); }
            catch (Exception) { } // A program may already have exited.
            finally { baseline.TrySetResult(); }
        }
        await started.ConfigureAwait(false);

        if (state == DebugState.Starting)
            state = DebugState.Running;
    }

    /// <summary>Goes on from where it stopped.</summary>
    public void Continue() => Go("continue");

    /// <summary>The next line, over calls.</summary>
    public void StepOver() => Go("next");

    /// <summary>The next line, into a call.</summary>
    public void StepIn() => Go("stepIn");

    /// <summary>Out of the call it's in.</summary>
    public void StepOut() => Go("stepOut");

    private void Go(string command, bool runTo = false, string? granularity = null)
    {
        if (state != DebugState.Stopped || stoppedThread < 0 || preparingRunTarget && !runTo)
            return;

        Interlocked.Increment(ref threadEpoch);
        state = DebugState.Running;
        happened.Enqueue(new DebugEvent(DebugEventKind.Continued, string.Empty, [], 0));
        var arguments = new JsonObject { ["threadId"] = stoppedThread };
        if (granularity is not null) arguments["granularity"] = granularity;
        _ = Quietly(connection.Request(command, arguments));
    }

    /// <summary>Stops it where it is.</summary>
    private int pausing;
    private bool pausesProcess;
    public void Pause()
    {
        if (state == DebugState.Running && Interlocked.CompareExchange(ref pausing, 1, 0) == 0)
            _ = PauseRunning();
    }

    private async Task PauseRunning()
    {
        long epoch = Volatile.Read(ref threadEpoch);
        try
        {
            // LLDB interrupts the process and ignores threadId. Its running-thread
            // enumeration can be empty; netcoredbg instead requires a real thread.
            if (pausesProcess)
            {
                await connection.Request("pause", new JsonObject { ["threadId"] = 0 }).ConfigureAwait(false);
                return;
            }
            var threads = await Threads().ConfigureAwait(false);
            if (state != DebugState.Running || epoch != Volatile.Read(ref threadEpoch)) return;
            int thread = threads.Any(thread => thread.Id == stoppedThread) ? stoppedThread : threads.FirstOrDefault().Id;
            if (thread <= 0) throw new InvalidOperationException("No running thread is available to pause.");
            await connection.Request("pause", new JsonObject { ["threadId"] = thread }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (state == DebugState.Running)
                happened.Enqueue(new DebugEvent(DebugEventKind.Output, "stderr\tCould not pause: " + error.Message + "\n", [], 0));
        }
        finally { Interlocked.Exchange(ref pausing, 0); }
    }

    /// <summary>Ends debugging, terminating an owned program or detaching from an external one.</summary>
    public void Stop() => _ = StopAsync();

    /// <summary>Stop or detach and wait for the adapter's disconnect acknowledgement.</summary>
    public Task StopAsync()
    {
        lock (stoppingLock)
            return stopping ??= state == DebugState.Ended && (TerminatesDebuggeeOnStop || connection.IsClosed)
                ? Task.CompletedTask : StopThenClose();
    }

    private async Task StopThenClose()
    {
        try
        {
            await connection.Request("disconnect", new JsonObject { ["terminateDebuggee"] = TerminatesDebuggeeOnStop, ["suspendDebuggee"] = false }).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // it's going either way
        }

        End(-1);
    }

    /// <summary>The scopes of a frame, its locals and the rest.</summary>
    public async Task<IReadOnlyList<DebugScope>> Scopes(int frameId)
    {
        var scopes = new List<DebugScope>();
        if (await connection.Request("scopes", new JsonObject { ["frameId"] = frameId }).ConfigureAwait(false) is JsonObject body && body["scopes"] is JsonArray list)
        {
            foreach (JsonNode? scope in list)
            {
                if (scope is JsonObject named)
                    scopes.Add(new DebugScope(named["name"]?.GetValue<string>() ?? "?", JsonNumbers.Int(named["variablesReference"], 0)));
            }
        }

        return scopes;
    }

    private static bool IsReadOnly(JsonNode value) => value["presentationHint"]?["attributes"] is JsonArray attributes
        && attributes.Any(attribute => Text(attribute) is "readOnly" or "constant");

    /// <summary>The variables of a scope or of a variable with something inside it.</summary>
    public Task<IReadOnlyList<DebugVariable>> Variables(int reference) => Variables(reference, hexadecimal: false);

    public async Task<IReadOnlyList<DebugVariable>> Variables(int reference, bool hexadecimal, int? start = null, int? count = null, string? filter = null)
    {
        var variables = new List<DebugVariable>();
        var arguments = new JsonObject { ["variablesReference"] = reference };
        if (start is not null) arguments["start"] = start.Value;
        if (count is not null) arguments["count"] = count.Value;
        if (filter is not null) arguments["filter"] = filter;
        if (hexadecimal && Can("supportsValueFormattingOptions")) arguments["format"] = new JsonObject { ["hex"] = true };
        if (await connection.Request("variables", arguments).ConfigureAwait(false) is JsonObject body && body["variables"] is JsonArray list)
        {
            foreach (JsonNode? variable in list)
            {
                if (variable is JsonObject one)
                {
                    variables.Add(new DebugVariable(one["name"]?.GetValue<string>() ?? "?", one["value"]?.GetValue<string>() ?? string.Empty,
                        one["type"]?.GetValue<string>(), JsonNumbers.Int(one["variablesReference"], 0))
                        { ReadOnly = IsReadOnly(one), EvaluateName = one["evaluateName"]?.GetValue<string>(), MemoryReference = one["memoryReference"]?.GetValue<string>(),
                            IndexedVariables = one["indexedVariables"] is {} indexed ? JsonNumbers.Int(indexed, 0) : null,
                            NamedVariables = one["namedVariables"] is {} named ? JsonNumbers.Int(named, 0) : null });
                }
            }
        }

        return variables;
    }

    public bool CanSetVariable => Can("supportsSetVariable");

    public bool CanSetExpression => Can("supportsSetExpression");

    /// <summary>Assigns a value to a watched expression in the selected frame using the adapter's evaluator.</summary>
    public async Task SetExpression(string expression, string value, int frameId)
    {
        if (!CanSetExpression || State != DebugState.Stopped) throw new InvalidOperationException("The debugger cannot edit expressions now.");
        await connection.Request("setExpression", new JsonObject { ["expression"] = expression, ["value"] = value, ["frameId"] = frameId }).ConfigureAwait(false);
    }

    public async Task SetVariable(int reference, string name, string value)
    {
        if (!CanSetVariable || State != DebugState.Stopped) throw new InvalidOperationException("The debugger cannot edit variables now.");
        await connection.Request("setVariable", new JsonObject { ["variablesReference"] = reference, ["name"] = name, ["value"] = value }).ConfigureAwait(false);
    }

    /// <summary>What an expression comes to in a frame, or what the debugger said it couldn't.</summary>
    public async Task<string> Evaluate(string expression, int frameId)
    {
        try
        {
            var body = await connection.Request("evaluate", new JsonObject { ["expression"] = expression, ["frameId"] = frameId, ["context"] = "repl" }).ConfigureAwait(false);
            return body?["result"]?.GetValue<string>() ?? string.Empty;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// What an expression comes to in a frame as a variable, the expression its name, what's inside it its reference, null
    /// when the debugger couldn't say. The context is DAP's, "watch", "hover" or "repl".
    /// </summary>
    public async Task<DebugVariable?> TryEvaluate(string expression, int frameId, string context = "watch") =>
        (await EvaluateSaying(expression, frameId, context).ConfigureAwait(false)).Value;

    /// <summary>
    /// What an expression comes to in a frame as a variable, or what the debugger said when it couldn't, its own message
    /// (netcoredbg says the name x does not exist in the current context). The context is DAP's, "watch", "hover" or "repl".
    /// </summary>
    public async Task<DebugEvaluation> EvaluateSaying(string expression, int frameId, string context = "watch")
    {
        try
        {
            if (await connection.Request("evaluate", new JsonObject { ["expression"] = expression, ["frameId"] = frameId, ["context"] = context }).ConfigureAwait(false) is not JsonObject body)
                return new DebugEvaluation(null, null);

            return new DebugEvaluation(new DebugVariable(expression, body["result"]?.GetValue<string>() ?? string.Empty, body["type"]?.GetValue<string>(), JsonNumbers.Int(body["variablesReference"], 0)) { ReadOnly = IsReadOnly(body), IndexedVariables = body["indexedVariables"] is {} indexed ? JsonNumbers.Int(indexed, 0) : null,
                NamedVariables = body["namedVariables"] is {} named ? JsonNumbers.Int(named, 0) : null, MemoryReference = body["memoryReference"]?.GetValue<string>() }, null);
        }
        catch (Exception e)
        {
            return new DebugEvaluation(null, e.Message);
        }
    }

    /// <summary>The next thing that happened, on the program's thread, false when nothing has.</summary>
    public bool TryTake(out DebugEvent next) => happened.TryDequeue(out next);

    // The adapter's events, on the reading thread
    private void Happened(string name, JsonNode? body)
    {
        switch (name)
        {
            case "module":
                ModuleChanged(body);
                break;
            case "initialized":
                initialized.TrySetResult();
                break;

            case "stopped":
                Interlocked.Increment(ref threadEpoch);
                stoppedThread = JsonNumbers.Int(body?["threadId"], stoppedThread);
                // lldb-dap says what an exception was in the description and leaves the text out
                _ = Stopped(body?["reason"]?.GetValue<string>() ?? "stopped", body?["text"]?.GetValue<string>() ?? body?["description"]?.GetValue<string>(), HitFunctionBreakpoint(body) || HitDataBreakpoint(body) || HitInstructionBreakpoint(body) || HitSourceReferenceBreakpoint(body));
                break;

            case "continued":
                // Go already retired the paused context. Its later acknowledgement must not discard a Pause
                // that is enumerating running threads. An externally initiated resume still retires the context.
                if (state != DebugState.Running) Interlocked.Increment(ref threadEpoch);
                if (state != DebugState.Ended)
                    state = DebugState.Running;
                break;

            case "output":
                string category = body?["category"]?.GetValue<string>() ?? "console";
                string text = body?["output"]?.GetValue<string>() ?? string.Empty;
                if (category != "telemetry" && text.Length > 0)
                    happened.Enqueue(new DebugEvent(DebugEventKind.Output, category + "\t" + text, [], 0));
                break;

            case "exited":
                End(JsonNumbers.Int(body?["exitCode"], 0));
                break;

            case "terminated":
                End(-1);
                break;

            case "process":
                Follow(JsonNumbers.Int(body?["systemProcessId"], 0));
                break;

            case "breakpoint":
                FunctionMoved(body?["breakpoint"]);
                DataBreakpointMoved(body?["breakpoint"]);
                InstructionBreakpointMoved(body?["breakpoint"]);
                SourceReferenceBreakpointMoved(body?["breakpoint"], Text(body?["reason"]));
                Moved(body?["breakpoint"]);
                break;
        }
    }

    // Where it stopped, the stack of the thread that did, before anybody is told
    private async Task Stopped(string reason, string? text, bool hitNative)
    {
        long epoch = Volatile.Read(ref threadEpoch);
        int thread = stoppedThread;
        bool Current() => state != DebugState.Ended && epoch == Volatile.Read(ref threadEpoch);
        var frames = new List<DebugFrame>();
        try
        {
            frames.AddRange(await StackTrace(thread).ConfigureAwait(false));
        }
        catch (Exception)
        {
            // stopped somewhere it can't say, still stopped
        }

        // netcoredbg can enumerate a runtime helper first after attach. Pausing it suspends the process but gives
        // no managed stack. Inspect the other paused threads before presenting an empty call stack to the user.
        if (frames.Count == 0 && reason == "pause" && completionAdapter == CORECLR && Current())
        {
            try
            {
                foreach (var candidate in await Threads().ConfigureAwait(false))
                {
                    if (!Current()) return;
                    if (candidate.Id <= 0 || candidate.Id == thread) continue;
                    try { frames.AddRange(await StackTrace(candidate.Id).ConfigureAwait(false)); }
                    catch (Exception) { continue; } // An individual runtime thread may not have a managed stack.
                    if (frames.Count > 0) { thread = candidate.Id; break; }
                }
            }
            catch (Exception) { } // Keep a genuine empty stop if the adapter cannot enumerate paused threads.
        }
        if (!Current())
            return;
        stoppedThread = thread;

        // A breakpoint with a hit count, a message to log or a condition this debugger doesn't do itself is gone on from
        // here when it isn't to stop
        DebugConditionFailure? failed = null;
        if (!hitNative && reason == "breakpoint" && frames.Count > 0 && await GoesOnFrom(frames[0], why => failed = why).ConfigureAwait(false))
            return;

        try { await ClearRunTarget().ConfigureAwait(false); }
        catch (Exception error) { text = "Could not restore breakpoints: " + error.Message; }

        bool nativeThrow = windowsLldb && BreaksOnException("cpp_throw") && frames.Count > 0
            && frames[0].Name.Contains("_CxxThrowException", StringComparison.Ordinal);
        if (nativeThrow) reason = "exception";
        DebugException? exception = nativeThrow ? new DebugException("C++ exception", "Thrown C++ exception", null, false)
            : reason == "exception" ? await ExceptionNow().ConfigureAwait(false) : null;
        if (!Current())
            return;
        state = DebugState.Stopped;
        happened.Enqueue(new DebugEvent(DebugEventKind.Stopped, text is { Length: > 0 } ? $"{reason}, {text}" : reason, frames, 0, exception, failed));
    }

    // What the exception it stopped on was, asked of a debugger that answers exceptionInfo, null of one that doesn't
    private async Task<DebugException?> ExceptionNow()
    {
        if (!Can("supportsExceptionInfoRequest"))
            return null;

        try
        {
            if (await connection.Request("exceptionInfo", new JsonObject { ["threadId"] = stoppedThread }).ConfigureAwait(false) is not JsonObject body)
                return null;

            JsonNode? details = body["details"];
            string type = Text(details?["fullTypeName"]) ?? Text(body["exceptionId"])?.Replace("CLR/", string.Empty) ?? "?";
            string message = Text(details?["message"]) ?? Text(body["description"]) ?? string.Empty;
            return new DebugException(type, message, Text(details?["stackTrace"]), Text(body["breakMode"]) is "unhandled" or "userUnhandled");
        }
        catch (Exception)
        {
            // it stopped on one all the same
            return null;
        }
    }

    // A JSON string as one, null for anything else
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;

    // The program itself, by its process id, held from when the debugger says it started so its own exit code can be read
    private Process? debuggee;

    private void Follow(int processId)
    {
        if (processId <= 0 || debuggee is not null)
            return;

        try
        {
            debuggee = Process.GetProcessById(processId);
            // The handle opened now, while it's there, an exit code is read through it once it's gone
            _ = debuggee.SafeHandle;
        }
        catch (Exception)
        {
            debuggee = null;
        }
    }

    /// <summary>
    /// The program's own exit code once it's gone, or what the debugger said. netcoredbg says 0 for a program an unhandled
    /// exception ended, the program says what it was, and a debugger that went away says nothing at all.
    /// </summary>
    private int ExitCodeOf(int said)
    {
        try
        {
            if (debuggee is { } program && program.WaitForExit(500))
                return program.ExitCode;
        }
        catch (Exception)
        {
            // never opened, or a system that won't say of a process it didn't start
        }

        return said;
    }

    private void End(int exitCode, string why = "")
    {
        if (state == DebugState.Ended)
            return;

        Interlocked.Increment(ref threadEpoch);
        state = DebugState.Ended;
        ClearModules();
        ClearSourceReferenceBreakpoints();
        initialized.TrySetResult();
        threadBaseline?.TrySetResult();
        happened.Enqueue(new DebugEvent(DebugEventKind.Ended, why, [], ExitCodeOf(exitCode)));
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // a step the debugger wouldn't take leaves it where it was, and it says so in what it writes
        }
    }

    public void Dispose()
    {
        // Closing an adapter while paused must resume an external process too. Share a disconnect already in flight
        // with cancellation/Stop so Dispose cannot tear down its pipes before the adapter has detached.
        if (!TerminatesDebuggeeOnStop) StopAsync().GetAwaiter().GetResult();
        End(-1);
        connection.Dispose();
        try
        {
            if (adapter is { HasExited: false })
                adapter.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // gone already
        }

        adapter?.Dispose();
        debuggee?.Dispose();
    }
}
