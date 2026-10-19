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
public class DebugSessionTests
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

    private static JsonObject ReadRequest(Stream from)
    {
        var header = new StringBuilder();
        int length = -1;
        while (true)
        {
            int b = from.ReadByte();
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
    }

    // netcoredbg, wherever it is on this machine, null when it isn't
    private static string? Netcoredbg()
    {
        if (Environment.GetEnvironmentVariable("HEX_NETCOREDBG") is { Length: > 0 } said && File.Exists(said))
            return said;

        string beside = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "hex", "Hex", "bin", "Debug", "net10.0", "netcoredbg", "netcoredbg.exe"));
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
                    <TargetFramework>net10.0</TargetFramework>
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
