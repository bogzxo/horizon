using System.Text;

using Horizon.HIDL;
using Horizon.HIDL.Runtime;
using Horizon.Webhost;

using ImGuiNET;

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
    private string inputBuffer = string.Empty;

    internal delegate void OnCommandProcessed(IWebSocketPacket result);

    internal event OnCommandProcessed? CommandProcessed;

    public override void Initialize()
    {
        Name = "Developer Console";
        commandHistory = new(128);

        Runtime.GlobalScope.DeclareSystem("_PRINT_LN", new NativeFunctionValue((args, env) =>
        {
            StringBuilder sb = new();
            for (int i = 0; i < args.Length; i++)
                sb.Append(args[i].ToString() + " ");

            SendCommand(sb.ToString());
            return new NullValue();
        }));
        Runtime.GlobalScope.DeclareSystem("_CLEAR_SCR", new NativeFunctionValue((args, env) =>
        {
            commandHistory.Clear();
            CommandProcessed?.Invoke(new CommandLinePacket()
            {
                SpecialPacket = true,
                Message = "clear"
            });
            return new NullValue();
        }));

        Runtime.GlobalScope.DeclareSystem("help", new NativeFunctionValue((args, env) =>
        {
            return new StringValue("test");
        }));
    }

    public override void Render(float dt, object? obj = null)
    {
        if (Visible && ImGui.Begin("Developer Console", ImGuiWindowFlags.NoCollapse))
        {
            // Draw command history
            ImGui.BeginChild("CommandHistory", new System.Numerics.Vector2(0, -ImGui.GetTextLineHeightWithSpacing()));
            for (int i = 0; i < commandHistory.Count; i++)
            {
                (bool resp, string msg) = commandHistory[i];
                ImGui.Text(resp ? ">" : "<");
                ImGui.SameLine();
                ImGui.TextWrapped(msg);
            }
            ImGui.EndChild();

            // Draw input field
            ImGui.Text(">");
            ImGui.SameLine();
            bool enter = ImGui.InputText("##InputField", ref inputBuffer, 256, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if (enter || ImGui.Button("Execute"))
            {
                ExecuteCommand(inputBuffer.ToString());
                inputBuffer = string.Empty;
            }

            ImGui.End();
        }
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

    private void SendCommand(string text)
    {
        CommandLinePacket final = new()
        {
            PacketID = 1,
            IsResponse = true,
            Message = text.CompareTo("null") == 0 ? "" : text
        };

        commandHistory.Add(final);
        CommandProcessed?.Invoke(final);
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
}