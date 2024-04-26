using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Text;

using Horizon.Horlang;
using Horizon.Horlang.Runtime;
using Horizon.Webhost;

using ImGuiNET;

using Microsoft.Win32;

namespace Horizon.Engine.Debugging.Debuggers;

public class DeveloperConsole : DebuggerComponent
{
    internal readonly struct CommandLinePacket(in string msg, in bool resp) : IWebSocketPacket
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

    public HorlangRuntime Runtime { get; init; } = new();

    private List<CommandLinePacket> commandHistory;
    private string inputBuffer = string.Empty;
    internal delegate void OnCommandProcessed(IWebSocketPacket result);
    internal event OnCommandProcessed? CommandProcessed;

    public override void Initialize()
    {
        Name = "Developer Console";
        commandHistory = new(128);

        Runtime.Environment.Declare("_PRINT_LN", new NativeFunctionValue((args, env) =>
        {
            StringBuilder sb = new();
            for (int i = 0; i < args.Length; i++)
                sb.Append(args[i].ToString() + " ");

            SendCommand(sb.ToString());
            return new NullValue();
        }));
        Runtime.Environment.Declare("_CLEAR_SCR", new NativeFunctionValue((args, env) =>
        {
            commandHistory.Clear();
            CommandProcessed?.Invoke(new CommandLinePacket()
            {
                SpecialPacket = true,
                Message = "clear"
            });
            return new NullValue();
        }));

        Runtime.Environment.Declare("help", new NativeFunctionValue((args, env) =>
        {
            return new StringValue("test");
        }));

        Runtime.Evaluate(@"
let env = {
    print: _PRINT_LN,
    clear: _CLEAR_SCR
};
");
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

        string returnVal = Runtime.Evaluate(input);
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
}