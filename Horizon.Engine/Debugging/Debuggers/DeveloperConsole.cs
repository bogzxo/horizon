using System.Text;
using Bogz.Logging;
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
    private readonly struct CommandLinePacket(in string msg, in bool resp) : IWebSocketPacket
    {
        public bool SpecialPacket { get; init; }
        public uint PacketID { get; init; } = 1;
        public readonly bool IsResponse { get; init; } = resp;
        public readonly string Message { get; init; } = msg;

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

    public override void Initialize()
    {
        Name = "Developer Console";
        commandHistory = [with(128)];

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
        if (silent) return;
        commandHistory.Add(final);
    }

    public override void UpdatePhysics(float dt)
    {
    }

    public override void UpdateState(float dt)
    {
    }

    public override void Dispose()
    {
    }

    internal void EvaluateCallback(string obj)
    {
        ExecuteCommand(obj);
    }

    public void Log(string text) => SendCommand(text);
}
