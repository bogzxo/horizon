using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>Where a debugged program is, one call of its stack, the line and column counted from 0.</summary>
public readonly record struct DebugFrame(int Id, string Name, string? File, int Line, int Column);

/// <summary>A scope of a frame (its locals, its arguments), whose variables are asked for by the reference.</summary>
public readonly record struct DebugScope(string Name, int Reference);

/// <summary>A variable and what it is, with a reference to ask for what's inside it when there is anything, 0 when not.</summary>
public readonly record struct DebugVariable(string Name, string Value, string? Type, int Reference);

/// <summary>What happened in a debug session, taken on the program's own thread (<see cref="DebugSession.TryTake"/>).</summary>
public readonly record struct DebugEvent(DebugEventKind Kind, string Text, IReadOnlyList<DebugFrame> Frames, int ExitCode);

public enum DebugEventKind
{
    /// <summary>It stopped (a breakpoint, a step, a pause, an exception), <see cref="DebugEvent.Text"/> says why and the frames are where.</summary>
    Stopped,

    /// <summary>It's going again.</summary>
    Continued,

    /// <summary>Something it or the debugger wrote, the category (stdout, stderr, console) before a tab in the text.</summary>
    Output,

    /// <summary>It's over, with the program's exit code when it said one.</summary>
    Ended
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
public sealed class DebugSession : IDisposable
{
    private readonly DebugAdapterConnection connection;
    private readonly Process? adapter;
    private readonly ConcurrentQueue<DebugEvent> happened = new();
    private readonly Dictionary<string, int[]> breakpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile DebugState state = DebugState.Starting;
    private int stoppedThread = -1;

    /// <summary>Where it is.</summary>
    public DebugState State => state;

    /// <summary>What went wrong starting it, null while nothing did.</summary>
    public string? Failed { get; private set; }

    private DebugSession(DebugAdapterConnection connection, Process? adapter)
    {
        this.connection = connection;
        this.adapter = adapter;
        connection.OnEvent = Happened;
        connection.Closed = _ => End(-1);
    }

    /// <summary>Starts a debug adapter, null when it can't be started (not there, not a program).</summary>
    public static DebugSession? Start(string command, IReadOnlyList<string> arguments)
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
            return new DebugSession(new DebugAdapterConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream), process);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A session over streams an adapter is on the other end of, for a test.</summary>
    public static DebugSession Over(Stream input, Stream output) => new(new DebugAdapterConnection(input, output), null);

    /// <summary>The kind of debugger netcoredbg is, .NET's, the one <see cref="Launch"/> goes by when it isn't told.</summary>
    public const string CORECLR = "coreclr";

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
    public async Task<bool> Launch(string program, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, IReadOnlyCollection<int>> breakpointsByFile, string kind = CORECLR, string? output = null)
    {
        try
        {
            await connection.Request("initialize", new JsonObject
            {
                ["clientID"] = "hex",
                ["clientName"] = "Hex",
                ["adapterID"] = kind,
                ["linesStartAt1"] = true,
                ["columnsStartAt1"] = true,
                ["pathFormat"] = "path",
                ["supportsVariableType"] = true
            }).ConfigureAwait(false);

            var args = new JsonArray();
            foreach (string argument in arguments)
                args.Add((JsonNode?)JsonValue.Create(argument));

            // The launch goes off and is answered once the configuration is done, which the adapter asks for with an
            // initialized event once it's ready for the breakpoints
            JsonObject launch = kind == CORECLR
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
                    ["justMyCode"] = true
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

            // What it writes into a file, stdin left as it is and stdout and stderr both there
            if (output is not null)
                launch["stdio"] = new JsonArray(null, output, output);
            Task<JsonNode?> launched = connection.Request("launch", launch);

            Task first = await Task.WhenAny(initialized.Task, launched, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(false);
            if (first == launched)
                await launched.ConfigureAwait(false);

            foreach (var (file, lines) in breakpointsByFile)
                await SetBreakpointsNow(file, lines).ConfigureAwait(false);
            await connection.Request("configurationDone").ConfigureAwait(false);
            await launched.ConfigureAwait(false);

            if (state == DebugState.Starting)
                state = DebugState.Running;
            return true;
        }
        catch (Exception e)
        {
            Failed = e.Message;
            End(-1);
            return false;
        }
    }

    /// <summary>The breakpoints of a file, lines counted from 0, all of them (an empty list takes them all away).</summary>
    public void SetBreakpoints(string file, IReadOnlyCollection<int> lines)
    {
        if (state is DebugState.Starting or DebugState.Ended)
            return;
        _ = SetBreakpointsQuietly(file, lines);
    }

    private async Task SetBreakpointsQuietly(string file, IReadOnlyCollection<int> lines)
    {
        try
        {
            await SetBreakpointsNow(file, lines).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the debugger refusing one isn't the end of the session
        }
    }

    private async Task SetBreakpointsNow(string file, IReadOnlyCollection<int> lines)
    {
        lock (breakpoints)
            breakpoints[file] = [.. lines];

        var list = new JsonArray();
        foreach (int line in lines)
            list.Add((JsonNode)new JsonObject { ["line"] = line + 1 });
        await connection.Request("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = file, ["name"] = Path.GetFileName(file) },
            ["breakpoints"] = list
        }).ConfigureAwait(false);
    }

    /// <summary>Goes on from where it stopped.</summary>
    public void Continue() => Go("continue");

    /// <summary>The next line, over calls.</summary>
    public void StepOver() => Go("next");

    /// <summary>The next line, into a call.</summary>
    public void StepIn() => Go("stepIn");

    /// <summary>Out of the call it's in.</summary>
    public void StepOut() => Go("stepOut");

    private void Go(string command)
    {
        if (state != DebugState.Stopped || stoppedThread < 0)
            return;

        state = DebugState.Running;
        happened.Enqueue(new DebugEvent(DebugEventKind.Continued, string.Empty, [], 0));
        _ = Quietly(connection.Request(command, new JsonObject { ["threadId"] = stoppedThread }));
    }

    /// <summary>Stops it where it is.</summary>
    public void Pause()
    {
        if (state == DebugState.Running)
            _ = Quietly(connection.Request("pause", new JsonObject { ["threadId"] = stoppedThread >= 0 ? stoppedThread : 0 }));
    }

    /// <summary>Ends it, the program and the debugger with it.</summary>
    public void Stop()
    {
        if (state == DebugState.Ended)
            return;

        _ = StopThenClose();
    }

    private async Task StopThenClose()
    {
        try
        {
            await connection.Request("disconnect", new JsonObject { ["terminateDebuggee"] = true }).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
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

    /// <summary>The variables of a scope or of a variable with something inside it.</summary>
    public async Task<IReadOnlyList<DebugVariable>> Variables(int reference)
    {
        var variables = new List<DebugVariable>();
        if (await connection.Request("variables", new JsonObject { ["variablesReference"] = reference }).ConfigureAwait(false) is JsonObject body && body["variables"] is JsonArray list)
        {
            foreach (JsonNode? variable in list)
            {
                if (variable is JsonObject one)
                {
                    variables.Add(new DebugVariable(one["name"]?.GetValue<string>() ?? "?", one["value"]?.GetValue<string>() ?? string.Empty,
                        one["type"]?.GetValue<string>(), JsonNumbers.Int(one["variablesReference"], 0)));
                }
            }
        }

        return variables;
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
    public async Task<DebugVariable?> TryEvaluate(string expression, int frameId, string context = "watch")
    {
        try
        {
            if (await connection.Request("evaluate", new JsonObject { ["expression"] = expression, ["frameId"] = frameId, ["context"] = context }).ConfigureAwait(false) is not JsonObject body)
                return null;

            return new DebugVariable(expression, body["result"]?.GetValue<string>() ?? string.Empty, body["type"]?.GetValue<string>(), JsonNumbers.Int(body["variablesReference"], 0));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The next thing that happened, on the program's thread, false when nothing has.</summary>
    public bool TryTake(out DebugEvent next) => happened.TryDequeue(out next);

    // The adapter's events, on the reading thread
    private void Happened(string name, JsonNode? body)
    {
        switch (name)
        {
            case "initialized":
                initialized.TrySetResult();
                break;

            case "stopped":
                stoppedThread = JsonNumbers.Int(body?["threadId"], stoppedThread);
                // lldb-dap says what an exception was in the description and leaves the text out
                _ = Stopped(body?["reason"]?.GetValue<string>() ?? "stopped", body?["text"]?.GetValue<string>() ?? body?["description"]?.GetValue<string>());
                break;

            case "continued":
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
        }
    }

    // Where it stopped, the stack of the thread that did, before anybody is told
    private async Task Stopped(string reason, string? text)
    {
        var frames = new List<DebugFrame>();
        try
        {
            if (await connection.Request("stackTrace", new JsonObject { ["threadId"] = stoppedThread, ["levels"] = 50 }).ConfigureAwait(false) is JsonObject body && body["stackFrames"] is JsonArray list)
            {
                foreach (JsonNode? frame in list)
                {
                    if (frame is not JsonObject one)
                        continue;
                    frames.Add(new DebugFrame(JsonNumbers.Int(one["id"], 0), one["name"]?.GetValue<string>() ?? "?", one["source"]?["path"]?.GetValue<string>(),
                        Math.Max(0, JsonNumbers.Int(one["line"], 1) - 1), Math.Max(0, JsonNumbers.Int(one["column"], 1) - 1)));
                }
            }
        }
        catch (Exception)
        {
            // stopped somewhere it can't say, still stopped
        }

        if (state == DebugState.Ended)
            return;
        state = DebugState.Stopped;
        happened.Enqueue(new DebugEvent(DebugEventKind.Stopped, text is { Length: > 0 } ? $"{reason}, {text}" : reason, frames, 0));
    }

    private void End(int exitCode)
    {
        if (state == DebugState.Ended)
            return;

        state = DebugState.Ended;
        initialized.TrySetResult();
        happened.Enqueue(new DebugEvent(DebugEventKind.Ended, string.Empty, [], exitCode));
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
    }
}
