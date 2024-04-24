using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Text;

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

    private List<CommandLinePacket> commandHistory;
    private Dictionary<string, Func<object>> globalVariables;
    private Dictionary<string, Func<string[], CommandLinePacket?>> commandMap;
    private Dictionary<string, string> commandInfoMap, localVariables;
    private Dictionary<string, int> registers;
    private List<string> stack;
    private bool editingStack;
    private int stackLineIndex = 0, stackIndex;
    private readonly string[] errorMessages = ["nuh uh", "nope", "stop.", "BAD", "are you being intentionally dense right now?", "err", "seg fault lmao"];
    private string inputBuffer = string.Empty;
    private bool haltStack = true;

    internal delegate void OnCommandProcessed(IWebSocketPacket result);
    internal event OnCommandProcessed? CommandProcessed;

    public override void Dispose()
    {

    }

    public override void Initialize()
    {
        Name = "Developer Console";
        commandHistory = new(128);
        stack = [];

        registers = [];
        for (int i = 0; i < 10; i++)
            registers.Add(((char)('A' + i)).ToString(), 0);

        globalVariables = new()
        {
            {"gametime", () => GameEngine.Instance.Runtime }
        };
        localVariables = new();

        commandInfoMap = new()
        {
            { "print", "prints ig what do you want." },
            { "stop", "closes Horizon." },
            { "clear", "clears the buffer." },
            { "goto", "changes the program stack pointer to the first argument." },
            { "editstack", "allows editing of a program stack." },
            { "viewstack", "prints the program stack." },
            { "clearstack", "clears the program stack." },
            { "set", "sets a register to an argument, #X for another register, $x for a global variable, @x for a local variable." },
            { "inc", "Increases a register by 1 (or the second argument)." },
            { "dec", "Decreases a register by 1 (or the second argument)." },
            { "viewregisters", "Prints all register values to the output buffer." },
            { "halt", "Suspends program stack execution." },
            { "run", "Begins execution of the program stack." },
            { "help", "oh you idiot" }
        };

        commandMap = new()
        {
            {
                "print",
                (args) => {
                    StringBuilder sb = new();
                    foreach (var arg in args)
                        sb.Append(ParseArgument(arg) + " ");

                    return new (sb.ToString(), true);
                }
            },
            {
                "stop",
                (_) => {
                    GameEngine.Instance.WindowManager.Window.Close();
                    return null;
                    return new ("ok", true);
                }
            },
            {
                "clear",
                (_) => new() { IsResponse = true, SpecialPacket = true, Message = "clear" }
            },
            {
                "goto",
                (args) =>
                {
                    stackIndex = int.Parse(ParseArgument(args[0]));
                    return null;
                    return new (stackIndex.ToString(), true);
                }
            },  {
                "jump",
                (args) =>
                {
                    if (ParseArgument(args[1]) != "0")
                        stackIndex = int.Parse(ParseArgument(args[0]));
                    return null;
                    return new (stackIndex.ToString(), true);
                }
            },
            {
                "editstack",
                (args) => editStack(args)
            },
            {
                "viewstack",
                (_) =>  new(string.Join('\n', stack.Select((val, index) => { return $">{index}. {val}"; })), true)
            },
            {
                "clearstack",
                (_) => { stack.Clear();stack = new(); return null; return new ("stack cleared.", true); }
            },
            {
                "set",
                (args) => {

                    if (args.Length != 2) return new (GetRandomError(" set has invalid args."), true);

                    if (registers.ContainsKey(args[0]))
                        registers[args[0]] = int.Parse(ParseArgument(args[1]));
                    else return new (GetRandomError(" register is invalid."), true);

                    return null;
                    return new ($"Register[{args[0]}]: {args[1]}", true);
                }
            },
             {
                "inc",
                (args) => {

                    if (args.Length < 1 || args.Length > 2) return new (GetRandomError(" set has invalid args."), true);

                    if (registers.ContainsKey(args[0]))
                        registers[args[0]] += (args.Length > 1) ? int.Parse(ParseArgument(args[1])) : 1;
                    else return new (GetRandomError(" register is invalid."), true);
                    return null;
                    return new ($"Register[{args[0]}]: {registers[args[0]]}", true);
                }
            },
             {
                "dec",
                (args) => {

                    if (args.Length < 1 || args.Length > 2) return new (GetRandomError(" set has invalid args."), true);

                    if (registers.ContainsKey(args[0]))
                        registers[args[0]] -= (args.Length > 1) ? int.Parse(ParseArgument(args[1])) : 1;
                    else return new (GetRandomError(" register is invalid."), true);
                    return null;
                    return new ($"Register[{args[0]}]: {registers[args[0]]}", true);
                }
            },
            {
                "viewregisters",
                (_) =>  new("\t>" + string.Join("\n\t", registers.Select((val, index) => { return $"Register[{((char)('A' + index))}]: {registers[((char)('A' + index)).ToString()]}"; })), true)
            },
            {
                "run",
                (_) => {
                    haltStack = false;
                    Task.Run(runStack);
                    return new("beginning stack execution.", true);
                }
            },
            {
                "halt",
                (_) => {
                    haltStack = true;
                    return new("halt!", true);
                }
            }
        };

        commandMap.Add("help", (args) =>
        {
            string padding(in string input, int val = 20)
            {
                StringBuilder sb = new();
                sb.Append(input);
                while (sb.Length < val) sb.Append(' ');
                return sb.ToString();
            }
            if (args.Length == 0)
            {
                StringBuilder sb = new();

                foreach (var command in commandMap.Keys)
                {
                    if (sb.Length > 0) sb.Append(">");
                    if (commandInfoMap.TryGetValue(command, out string description))
                        sb.AppendLine(padding(command) + "\t" + description);
                    else sb.AppendLine(command);
                }

                return new(sb.ToString(), true);
            }

            return GenerateError();
        });
    }

    private async Task runStack()
    {
        stackIndex = 0;
        int counter = 0;
        while (!haltStack && counter < 100 && stackIndex < stack.Count)
        {
            counter++;

            int oldIndex = stackIndex;
            ExecuteCommand(stack[stackIndex]);
            if (oldIndex == stackIndex) stackIndex++;
            await Task.Delay(10);
        }
    }

    CommandLinePacket editStack(in string[] _ = null)
    {
        return new((editingStack = !editingStack) ? "in stack" : "left stack", true);
    }

    private CommandLinePacket GenerateError() => new(GetRandomError(), true);
    private string GetRandomError(in string? message = null) => errorMessages[Random.Shared.Next(0, errorMessages.Length - 1)] + (message is null ? string.Empty : message);

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
        string[] parts = input.TrimStart().Split(' ');
        if (parts.Length < 1) return;

        string command = parts[0];
        CommandLinePacket? final = null;

        // special case for stack editing
        if (editingStack)
        {
            if (input.CompareTo("editstack") == 0)
                final = editStack();
            else
            {
                if (char.IsDigit(input[0]))
                {
                    if (int.TryParse(input[..input.IndexOf('.')], out int number))
                    {
                        if (number < 0)
                        {
                            final = new(GetRandomError($" Cannot use line number '{number}'"), true);
                        }
                        else
                        {
                            string parsedCommand = input[(input.IndexOf('.') + 1)..].Trim();
                            AddToStack(parsedCommand, number);
                            stackLineIndex = number;
                            final = new($"Pushed '{parsedCommand}'", true);
                        }
                    }
                }
                else
                {
                    AddToStack(input, ++stackLineIndex);
                    final = new($"Pushed '{input}'", true);
                }

            }
        }
        else
        {
            if (commandMap.TryGetValue(command, out var func))
                final = func(parts[1..]);
        }

        if (final is not null)
        {
            commandHistory.Add(final.Value);
            if (final.Value.IsResponse) CommandProcessed?.Invoke(final);
        }
    }

    private void AddToStack(in string command, in int number)
    {
        if (number < stack.Count - 1)
            stack[number - 1] = command;
        else
        {
            for (int i = stack.Count; i < number; i++)
            {
                stack.Add("");
            }
            stack.Add(command);
        }
        stackLineIndex = number;
    }
    private string ParseArgument(string arg)
    {
        // ensure we are atleast two in length
        if (arg.CompareTo(string.Empty) == 0 || arg.Length < 2) return arg;

        // extract precursor
        char precursor = arg[0];
        string command = arg[1..];

        // different precursors can do different things
        switch (precursor)
        {
            // The case of returning a game variable
            case '$':
                if (globalVariables.TryGetValue(command, out var varVal))
                {
                    var val = varVal().ToString() ?? "0.0";
                    return val[..val.IndexOf('.')];
                }
                break;
            case '#':
                if (registers.TryGetValue(command, out var regVal))
                {
                    return regVal.ToString();
                }
                break;
        }

        string parsed = arg.Trim();
        if (int.TryParse(parsed, out var valInt))
            return ((int)valInt).ToString();
        if (float.TryParse(parsed, out var valFloat))
            return ((int)valFloat).ToString();
        if (double.TryParse(parsed, out var valDouble))
            return ((int)valDouble).ToString();
        if (decimal.TryParse(parsed, out var valDecimal))
            return ((int)valDecimal).ToString();


        return "0";
    }

    public override void UpdatePhysics(float dt)
    {

    }

    public override void UpdateState(float dt)
    {

    }
}