using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Horizon.UI.Scripting;

/// <summary>How a breakpoint's hit count decides it, Visual Studio's three.</summary>
public enum DebugHitKind
{
    /// <summary>On that hit and no other.</summary>
    Equal,

    /// <summary>On that hit and every one after.</summary>
    AtLeast,

    /// <summary>On every hit that's a multiple of it.</summary>
    MultipleOf
}

/// <summary>A breakpoint's hit count, what kind and the count, which stops it on the hits that pass and goes on from the rest.</summary>
public readonly record struct DebugHitCondition(DebugHitKind Kind, int Count)
{
    /// <summary>Whether a hit, counted from 1, is one it stops on.</summary>
    public bool Stops(int hit) => Kind switch
    {
        DebugHitKind.Equal => hit == Count,
        DebugHitKind.AtLeast => hit >= Count,
        _ => Count > 0 && hit % Count == 0
    };

    /// <summary>The way DAP's hitCondition writes it, VS Code's (5, >=5, %5).</summary>
    public override string ToString() => Kind switch
    {
        DebugHitKind.Equal => Count.ToString(CultureInfo.InvariantCulture),
        DebugHitKind.AtLeast => ">=" + Count.ToString(CultureInfo.InvariantCulture),
        _ => "%" + Count.ToString(CultureInfo.InvariantCulture)
    };
}

/// <summary>
/// A breakpoint on a line counted from 0, with what can be said of it the way Visual Studio's can, a condition it only
/// stops on when it's true, a hit count, and a message to log instead of stopping, with {expressions} in it evaluated.
/// </summary>
public readonly record struct DebugBreakpoint(int Line, string? Condition = null, DebugHitCondition? Hits = null, string? LogMessage = null);

/// <summary>
/// A breakpoint's condition that couldn't be evaluated where it stopped, or came to something that isn't true or false.
/// It stops there, the way Visual Studio stops and says the condition failed rather than going on as if it were false.
/// </summary>
/// <param name="File">The file of the breakpoint.</param>
/// <param name="Line">Its line as it was asked for, counted from 0.</param>
/// <param name="Condition">The condition as it was written.</param>
/// <param name="Error">What the evaluation said went wrong, null when it came to something.</param>
/// <param name="CameTo">What it came to when that wasn't true or false, null when it couldn't be evaluated.</param>
public sealed record DebugConditionFailure(string File, int Line, string Condition, string? Error, string? CameTo = null);

// The breakpoints of a debug session. A condition goes to the debugger, which stops only where it's true (netcoredbg
// does them), unless an editor evaluates expressions its own way (Evaluates), when the condition is checked here at each
// hit with that, and so are the expressions of a message to log. netcoredbg alone said "The name 'args.Length' does not
// exist" of a condition Immediate evaluated fine. Hit counts and messages to log are this side's, netcoredbg takes
// hitCondition and logMessage and stops on every hit all the same, and lldb-dap reads a hit condition as a bare number.
// So a stop on one of those is counted here and gone on from when it isn't one to stop on, the message said in the output
// on the way.
public sealed partial class DebugSession
{
    // What the adapter said it can do, in its answer to initialize
    private JsonObject? capabilities;

    // The breakpoints of each file as they were asked for, each with its id and the line the debugger put it on
    private readonly Dictionary<string, Placed[]> breakpoints = new(StringComparer.OrdinalIgnoreCase);

    // How many times each breakpoint has been hit (passed its condition) this session, by its file and line asked for
    private readonly Dictionary<(string File, int Line), int> hits = [];

    private sealed class Placed(DebugBreakpoint wanted)
    {
        public DebugBreakpoint Wanted { get; } = wanted;
        public int Id { get; set; } = -1;
        public bool Verified { get; set; }
        public int Line { get; set; } = wanted.Line;
    }

    private void Capable(JsonNode? said) => capabilities = said as JsonObject;

    /// <summary>
    /// How the expressions of a message to log and a condition this side checks are evaluated, the frame they're in
    /// given. Null for the debugger's own evaluation as it is, an editor sets one that knows the file's names.
    /// </summary>
    public Func<string, DebugFrame, Task<DebugEvaluation>>? Evaluates { get; set; }

    private Task<DebugEvaluation> EvaluateIn(string expression, DebugFrame frame) =>
        Evaluates is { } evaluates ? evaluates(expression, frame) : EvaluateSaying(expression, frame.Id);

    // Whether the debugger checks the conditions and writes the messages to log itself, which it doesn't when an editor
    // evaluates expressions its own way
    private bool DebuggerConditions => Evaluates is null && Can("supportsConditionalBreakpoints");

    private bool DebuggerLogs => Evaluates is null && Can("supportsLogPoints");

    /// <summary>Whether the adapter said it can do something, a capability of DAP's by its name.</summary>
    public bool Can(string capability) => capabilities?[capability] is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    // Lines alone made breakpoints with nothing more to them
    private static Dictionary<string, IReadOnlyCollection<DebugBreakpoint>> Plain(IReadOnlyDictionary<string, IReadOnlyCollection<int>> byFile) =>
        byFile.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<DebugBreakpoint>)[.. pair.Value.Select(line => new DebugBreakpoint(line))], StringComparer.OrdinalIgnoreCase);

    /// <summary>The breakpoints of a file, lines counted from 0, all of them (an empty list takes them all away).</summary>
    public void SetBreakpoints(string file, IReadOnlyCollection<int> lines) =>
        SetBreakpoints(file, [.. lines.Select(line => new DebugBreakpoint(line))]);

    /// <summary>The breakpoints of a file with their conditions, hit counts and messages, all of them.</summary>
    public void SetBreakpoints(string file, IReadOnlyCollection<DebugBreakpoint> set)
    {
        if (state is DebugState.Starting or DebugState.Ended)
            return;
        _ = SetBreakpointsQuietly(file, set);
    }

    private async Task SetBreakpointsQuietly(string file, IReadOnlyCollection<DebugBreakpoint> set)
    {
        try
        {
            await SetBreakpointsNow(file, set).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the debugger refusing one isn't the end of the session
        }
    }

    private async Task SetBreakpointsNow(string file, IReadOnlyCollection<DebugBreakpoint> set)
    {
        lock (breakpoints)
        {
            if (runTarget is { } target && string.Equals(target.File, file, StringComparison.OrdinalIgnoreCase))
            {
                runTarget = target with { Original = set.ToArray() };
                set = set.Where(item => item.Line != target.Line).Append(new DebugBreakpoint(target.Line)).ToArray();
            }
        }
        Placed[] placed = [.. set.Select(wanted => new Placed(wanted))];
        lock (breakpoints)
            breakpoints[file] = placed;

        var list = new JsonArray();
        foreach (Placed one in placed)
        {
            var breakpoint = new JsonObject { ["line"] = one.Wanted.Line + 1 };
            if (one.Wanted.Condition is { Length: > 0 } condition && DebuggerConditions)
                breakpoint["condition"] = condition;
            if (one.Wanted.LogMessage is { Length: > 0 } log && DebuggerLogs)
                breakpoint["logMessage"] = log;
            list.Add((JsonNode)breakpoint);
        }

        JsonNode? answer = await connection.Request("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = file, ["name"] = Path.GetFileName(file) },
            ["breakpoints"] = list
        }).ConfigureAwait(false);

        // What it put where, in the order they were asked for, and moved later by its breakpoint events
        if (answer?["breakpoints"] is JsonArray said)
        {
            lock (breakpoints)
            {
                for (int i = 0; i < said.Count && i < placed.Length; i++)
                {
                    placed[i].Verified = said[i]?["verified"]?.GetValue<bool>() == true;
                    placed[i].Id = JsonNumbers.Int(said[i]?["id"], -1);
                    placed[i].Line = JsonNumbers.Int(said[i]?["line"], placed[i].Wanted.Line + 1) - 1;
                }
            }
        }
    }

    // A breakpoint the debugger put somewhere else (netcoredbg resolves a pending one once its code is loaded)
    private void Moved(JsonNode? breakpoint)
    {
        int id = JsonNumbers.Int(breakpoint?["id"], -1);
        if (id < 0 || breakpoint?["line"] is null)
            return;

        lock (breakpoints)
        {
            foreach (Placed[] file in breakpoints.Values)
            {
                foreach (Placed one in file)
                {
                    if (one.Id == id)
                        one.Line = JsonNumbers.Int(breakpoint["line"], one.Line + 1) - 1;
                }
            }
        }
    }

    // The breakpoint a frame is stopped on, by its file and the line it was asked for or put on
    private DebugBreakpoint? BreakpointAt(DebugFrame frame)
    {
        if (frame.File is not { } file)
            return null;

        lock (breakpoints)
        {
            if (!breakpoints.TryGetValue(file, out Placed[]? placed))
                return null;
            foreach (Placed one in placed)
            {
                if (one.Line == frame.Line || one.Wanted.Line == frame.Line)
                    return one.Wanted;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a stop at a breakpoint is gone on from rather than stopped at, a condition checked here that's false, a hit
    /// count it hasn't come to, or a message logged instead. Going on it says nothing else of the stop. A condition that
    /// couldn't be evaluated stops it, with why handed to <paramref name="failed"/>.
    /// </summary>
    private async Task<bool> GoesOnFrom(DebugFrame frame, Action<DebugConditionFailure> failed)
    {
        if (BreakpointAt(frame) is not { } breakpoint)
            return false;
        if (breakpoint.Condition is null && breakpoint.Hits is null && breakpoint.LogMessage is null)
            return false;

        if (breakpoint.Condition is { Length: > 0 } condition && !DebuggerConditions)
        {
            // A C# condition is a bool, only lldb's C has a number that isn't 0 for true
            bool bools = Evaluates is not null;
            DebugEvaluation met = await EvaluateIn(condition, frame).ConfigureAwait(false);
            switch (met.Value?.Value)
            {
                case { } value when IsFalse(value, bools):
                    return GoOnQuietly();
                case { } value when !Truthy(value, bools):
                    failed(new DebugConditionFailure(frame.File!, breakpoint.Line, condition, null, value));
                    return false;
                case null:
                    failed(new DebugConditionFailure(frame.File!, breakpoint.Line, condition, met.Error ?? "?"));
                    return false;
            }
        }

        int hit;
        lock (hits)
        {
            var key = (frame.File!, breakpoint.Line);
            hits[key] = hit = hits.GetValueOrDefault(key) + 1;
        }

        if (breakpoint.Hits is { } count && !count.Stops(hit))
            return GoOnQuietly();

        if (breakpoint.LogMessage is { Length: > 0 } log && !DebuggerLogs)
        {
            string said = await Logged(log, frame).ConfigureAwait(false);
            happened.Enqueue(new DebugEvent(DebugEventKind.Output, "console\t" + said + "\n", [], 0));
            return GoOnQuietly();
        }

        return false;
    }

    /// <summary>How many times a breakpoint has been hit this session (its condition true each time), 0 for none.</summary>
    public int HitsOf(string file, int line)
    {
        lock (hits)
            return hits.GetValueOrDefault((file, line));
    }

    // On from a stop nobody is told of, the thread that stopped going again
    private bool GoOnQuietly()
    {
        _ = Quietly(connection.Request("continue", new JsonObject { ["threadId"] = stoppedThread }));
        return true;
    }

    // Whether what a condition came to is true, a bool's true or a number that isn't 0 (lldb's C) where numbers go, and
    // whether it's false
    private static bool Truthy(string value, bool bools) =>
        value is "true" or "True" || (!bools && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number != 0.0);

    private static bool IsFalse(string value, bool bools) =>
        value is "false" or "False" || (!bools && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number == 0.0);

    /// <summary>
    /// A message to log with what's in its braces evaluated where it stopped, Visual Studio's tracepoint way, $FUNCTION the
    /// function it's in. What couldn't be evaluated says why in its place, never a value that isn't one.
    /// </summary>
    private async Task<string> Logged(string message, DebugFrame frame)
    {
        var said = new StringBuilder();
        foreach (LogPart part in LogParts(message))
        {
            if (!part.Evaluated)
            {
                said.Append(part.Text.Replace("$FUNCTION", frame.Name, StringComparison.Ordinal));
                continue;
            }

            DebugEvaluation value = await EvaluateIn(part.Text, frame).ConfigureAwait(false);
            said.Append(value.Value is { } got ? got.Value : $"<{part.Text}: {value.Error ?? "?"}>");
        }

        return said.ToString();
    }

    /// <summary>A piece of a message to log, its text or an expression from between its braces, {{ and }} braces of their own.</summary>
    public readonly record struct LogPart(string Text, bool Evaluated);

    /// <summary>A message to log cut into its text and the expressions in braces.</summary>
    public static IReadOnlyList<LogPart> LogParts(string message)
    {
        var parts = new List<LogPart>();
        var text = new StringBuilder();
        for (int i = 0; i < message.Length; i++)
        {
            char c = message[i];
            if ((c == '{' || c == '}') && i + 1 < message.Length && message[i + 1] == c)
            {
                text.Append(c);
                i++;
                continue;
            }

            int close = c == '{' ? message.IndexOf('}', i + 1) : -1;
            if (close < 0)
            {
                text.Append(c);
                continue;
            }

            if (text.Length > 0)
                parts.Add(new LogPart(text.ToString(), false));
            text.Clear();
            string expression = message[(i + 1)..close].Trim();
            if (expression.Length > 0)
                parts.Add(new LogPart(expression, true));
            i = close;
        }

        if (text.Length > 0)
            parts.Add(new LogPart(text.ToString(), false));
        return parts;
    }
}
