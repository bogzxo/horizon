using System.Collections.Concurrent;
using System.Text;
using Horizon.Logging;
using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Webhost;


namespace Horizon.Engine.Debugging.Debuggers;

/// <summary>
/// End user accessible console for interfacing with the executing program, callbacks to native functions and custom runtime variables can be assigned here.
/// </summary>
public class DeveloperConsole : DebuggerComponent
{
    /// <summary>
    /// A packet implementation for communicating with the JS frontend. TODO: Data packing: use the ID to determine special packets instead of strings.
    /// </summary>
    /// <param name="msg"></param>
    /// <param name="resp"></param>
    internal readonly struct CommandLinePacket(in string msg, in bool resp) : IWebSocketPacket
    {
        public bool SpecialPacket { get; init; }
        public uint PacketID { get; init; } = 1;
        public bool IsResponse { get; init; } = resp;
        public string Message { get; init; } = msg;

        internal void Deconstruct(out bool resp, out string msg)
        {
            resp = IsResponse;
            msg = Message;
        }
    }

    /// <summary>
    /// The runtime for the Horizon Engine integrated interpreted language runtime. Scene specific native callbacks may be configured here, remember to delete and scene specific delcarations on scene change.
    /// </summary>
    public HIDLRuntime Runtime { get; init; } = new();

    private List<CommandLinePacket> commandHistory;
    private string quickInputBuffer = string.Empty;
    private string scriptBuffer = string.Empty;

    internal delegate void OnCommandProcessed(IWebSocketPacket result);

    internal event OnCommandProcessed? CommandProcessed;

    /// <summary>
    /// Raised with everything that goes through the console: the commands that are run (false) and what came back
    /// from them (true). On the simulation thread, for whatever shows the console, see <see cref="ConsoleOverlay"/>.
    /// </summary>
    public event Action<string, bool>? Output;

    // Commands that came in from somewhere else (the dashboard, on whatever thread its socket feels like), run on the
    // simulation thread at the next update. Run straight away they'd poke at the game while it's halfway through a tick
    private readonly ConcurrentQueue<string> _pending = new();

    public override void Initialize()
    {
        Name = "Developer Console";
        commandHistory = new List<CommandLinePacket>(128);

        Runtime.GlobalScope.DeclareSystem("eng", new ObjectValue()
        {
            Properties =
                new Dictionary<string, IRuntimeValue>
                {
                    {
                        "print",
                        new NativeFunctionValue((values, _) =>
                        {
                            var msg =
                                $"[{Name}] {string.Join(", ", values.Where(x => !string.IsNullOrEmpty(x?.ToString())))}";
                            SendCommand(msg, true);
                            return new StringValue(msg);
                        })
                    },
                    {
                        "clear",
                        new NativeFunctionValue((args, env) =>
                        {
                            commandHistory.Clear();
                            CommandProcessed?.Invoke(new CommandLinePacket()
                            {
                                SpecialPacket = true,
                                Message = "clear"
                            });
                            return new NullValue();
                        })
                    }
                }
        });


        Runtime.GlobalScope.DeclareSystem("help",
            new NativeFunctionValue((args, env) => { return new StringValue("test"); }));
    }

    internal void ExecuteCommand(string input)
    {
        // Add command to history
        commandHistory.Add(new(input, false));
        CommandProcessed?.Invoke(commandHistory.Last());
        Output?.Invoke(input, false);

        // Parse command
        if (input.Length < 1) return;

        string returnVal = Runtime.Evaluate(input).result;
        SendCommand(returnVal);
    }

    private void SendCommand(string text, bool silent=false)
    {
        CommandLinePacket final = new()
        {
            PacketID = 1,
            IsResponse = true,
            Message = text.CompareTo("null") == 0 ? "" : text
        };

        CommandProcessed?.Invoke(final);
        Output?.Invoke(final.Message, true);
        if (silent) return;
        commandHistory.Add(final);
    }

    public override void UpdatePhysics(float dt)
    {
    }

    public override void UpdateState(float dt)
    {
        while (_pending.TryDequeue(out string? input))
            ExecuteCommand(input);
    }

    public override void Dispose()
    {
    }

    /// <summary>
    /// Has a command run at the next update, on the simulation thread. From any thread.
    /// </summary>
    public void Enqueue(string command) => _pending.Enqueue(command);

    internal void EvaluateCallback(string obj) => Enqueue(obj);

    public void Log(string text) => SendCommand(text);
}
