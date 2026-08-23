using Box2D.NetStandard.Common;
using Egui;
using Egui.Containers;
using Egui.Widgets;
using Horizon.Core;
using Horizon.Engine.Debugging.Debuggers;
using Horizon.HIDL.Runtime;

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

#if DEBUG
    //public RenderOptionsDebugger RenderOptionsDebugger { get; private set; }
    public SceneEntityDebugger SceneEntityDebugger { get; private set; }
    public LoadedContentDebugger LoadedContentDebugger { get; private set; }
    public DockedGameContainerDebugger GameContainerDebugger { get; private set; }
    public PerformanceProfilerDebugger PerformanceDebugger { get; private set; }
    public GeneralDebugger GeneralDebugger { get; private set; }
#endif
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
                (Console = AddComponent<DeveloperConsole>()),
#if DEBUG
            (LoadedContentDebugger = AddComponent<LoadedContentDebugger>()),
                (GameContainerDebugger = AddComponent<DockedGameContainerDebugger>()),
                (PerformanceDebugger = AddComponent<PerformanceProfilerDebugger>()),
                (GeneralDebugger = AddComponent<GeneralDebugger>()),
                (SceneEntityDebugger = AddComponent<SceneEntityDebugger>())
#endif
            ]
        );
    }

    private void DestroyDebugComponents()
    {
        foreach (var comp in _components)
        {
            RemoveComponent(comp);
            comp.Dispose();
        }
        _components.Clear();
    }

    public override void RenderUi(Ui root)
    {
        base.RenderUi(root);

        RenderToContainer =
            Enabled &&
            GameContainerDebugger is { Visible: true, FrameBuffer.Handle: > 0 };


        if (!Enabled)
        {
            if (_components.Count != 0)
                DestroyDebugComponents();

            return;
        }

        if (_components.Count == 0)
            CreateDebugComponents();

        new MenuBar().Ui(root, menuBar =>
        {
            menuBar.MenuButton(DebuggerCatagoryNames.Home, menuUi =>
            {
                if (menuUi.Button("Close").Clicked)
                {
                    GameEngine.Instance.WindowManager.Window.Close();
                }

                menuUi.Checkbox(
                    ref Console.Visible,
                    Console.Name
                );
            });
#if DEBUG
            menuBar.MenuButton(DebuggerCatagoryNames.Graphics, menuUi =>
            {
                menuUi.Checkbox(
                    ref GameContainerDebugger.Visible,
                    GameContainerDebugger.Name
                );
            });

            menuBar.MenuButton(DebuggerCatagoryNames.Metrics, menuUi =>
            {
                menuUi.Checkbox(
                    ref PerformanceDebugger.Visible,
                    PerformanceDebugger.Name
                );

                menuUi.Checkbox(
                    ref GeneralDebugger.Visible,
                    GeneralDebugger.Name
                );
            });

            menuBar.MenuButton(DebuggerCatagoryNames.Scene, menuUi =>
            {
                menuUi.Checkbox(
                    ref SceneEntityDebugger.Visible,
                    PerformanceDebugger.Name
                );
                menuUi.Label("SceneDebugger is very WIP.");
            });

            menuBar.MenuButton(DebuggerCatagoryNames.Content, menuUi =>
            {
                menuUi.Checkbox(
                    ref LoadedContentDebugger.Visible,
                    LoadedContentDebugger.Name
                );
            });
#endif
        });

        foreach (var comp in _components)
        {
            comp.RenderUi(root);
        }

        base.RenderUi(root);
    }

}