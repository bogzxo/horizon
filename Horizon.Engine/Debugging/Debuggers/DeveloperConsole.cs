using ImGuiNET;

namespace Horizon.Engine.Debugging.Debuggers;

public class DeveloperConsole : DebuggerComponent
{
    private List<string> commandHistory = new List<string>();
    private string inputBuffer = string.Empty;


    public override void Dispose()
    {

    }

    public override void Initialize()
    {
        Name = "Developer Console";
    }

    public override void Render(float dt, object? obj = null)
    {
        if (Visible && ImGui.Begin("Developer Console", ImGuiWindowFlags.NoCollapse))
        {
            // Draw command history
            ImGui.BeginChild("CommandHistory", new System.Numerics.Vector2(0, -ImGui.GetTextLineHeightWithSpacing()));
            foreach (var command in commandHistory)
            {
                ImGui.TextWrapped(command);
            }
            ImGui.EndChild();

            // Draw input field
            ImGui.InputText("##InputField", ref inputBuffer, 256, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if (ImGui.Button("Execute"))
            {
                ExecuteCommand(inputBuffer.ToString());
                inputBuffer = string.Empty;
            }

            ImGui.End();
        }
    }

    internal void ExecuteCommand(string command)
    {
        // Add command to history
        commandHistory.Add(command);

        // Here you can implement the logic to execute the entered command
        // For simplicity, let's just print the command to the console
        System.Console.WriteLine("Executing command: " + command);
    }

    public override void UpdatePhysics(float dt)
    {

    }

    public override void UpdateState(float dt)
    {

    }
}