using Horizon.Core;
using Horizon.Engine.Debugging.Debuggers;
using Horizon.HIDL.Runtime;

using ImGuiNET;

namespace Horizon.Engine.Debugging;

public class SkylineDebugger : Entity
{
    private readonly struct DebuggerCatagoryNames
    {
        public static string Home { get; } = "Horizon";
        public static string Graphics { get; } = "Graphics";
        public static string Metrics { get; } = "Metrics";
        public static string Scene { get; } = "Scene";
        public static string Content { get; } = "Content";
    }

    private List<DebuggerComponent> _components = new();
    private bool hasInitializedHIDLE = false;

    //public RenderOptionsDebugger RenderOptionsDebugger { get; private set; }
    public SceneEntityDebugger SceneEntityDebugger { get; private set; }

    public LoadedContentDebugger LoadedContentDebugger { get; private set; }
    public DockedGameContainerDebugger GameContainerDebugger { get; private set; }
    public PerformanceProfilerDebugger PerformanceDebugger { get; private set; }
    public GeneralDebugger GeneralDebugger { get; private set; }
    public DeveloperConsole Console { get; private set; }

    public bool RenderToContainer { get; private set; }

    public SkylineDebugger()
    {
        Name = "Debugger";
        CreateDebugComponents();
    }

    private void CreateDebugComponents()
    {
        _components.AddRange(
            [
                //(RenderOptionsDebugger = AddComponent<RenderOptionsDebugger>()),
                (SceneEntityDebugger = AddComponent<SceneEntityDebugger>()),
                (LoadedContentDebugger = AddComponent<LoadedContentDebugger>()),
                (GameContainerDebugger = AddComponent<DockedGameContainerDebugger>()),
                (PerformanceDebugger = AddComponent<PerformanceProfilerDebugger>()),
                (Console = AddComponent<DeveloperConsole>()),
                (GeneralDebugger = AddComponent<GeneralDebugger>()),
            ]
        );
    }

    private void DestroyDebugComponents()
    {
        foreach (var comp in _components)
        {
            Components.Remove(comp);
            comp.Dispose();
        }
        _components.Clear();
    }

    public override void Render(float dt, object? obj = null)
    {
        RenderToContainer = Enabled && GameContainerDebugger.Visible && GameContainerDebugger.FrameBuffer.Handle > 0;

        if (!hasInitializedHIDLE)
        {
            hasInitializedHIDLE = true;
            Console.Runtime.Evaluate(@"
const env = {
    print: _PRINT_LN,
    clear: _CLEAR_SCR,
    debugger: {
        general: {
            get_watch: _HORIZON_GENERALDEBUGGER_GET
        }
    }
};
", true);
        }

        if (!Enabled)
        {
            if (_components.Count != 0)
                DestroyDebugComponents();
            return;
        }
        if (_components.Count == 0)
            CreateDebugComponents();

        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu(DebuggerCatagoryNames.Home))
            {
                if (ImGui.MenuItem("Close"))
                    GameEngine.Instance.WindowManager.Window.Close();

                ImGui.MenuItem(Console.Name, "", ref Console.Visible);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu(DebuggerCatagoryNames.Graphics))
            {
                //ImGui.MenuItem(
                //    RenderOptionsDebugger.Name,
                //    "",
                //    ref RenderOptionsDebugger.Visible
                //);
                ImGui.MenuItem(
                    GameContainerDebugger.Name,
                    "",
                    ref GameContainerDebugger.Visible
                );
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu(DebuggerCatagoryNames.Metrics))
            {
                ImGui.MenuItem(PerformanceDebugger.Name, "", ref PerformanceDebugger.Visible);
                ImGui.MenuItem(GeneralDebugger.Name, "", ref GeneralDebugger.Visible);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu(DebuggerCatagoryNames.Scene))
            {
                ImGui.MenuItem(SceneEntityDebugger.Name, "", ref SceneEntityDebugger.Visible);
                ImGui.MenuItem(
                    "Debug Entire Instance",
                    "",
                    ref SceneEntityDebugger.DebugInstance
                );
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu(DebuggerCatagoryNames.Content))
            {
                ImGui.MenuItem(
                    LoadedContentDebugger.Name,
                    "",
                    ref LoadedContentDebugger.Visible
                );
                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
        }

        base.Render(dt, obj);
    }
}