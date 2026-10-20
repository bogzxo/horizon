using Horizon.Physics;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Engine.Debugging;

// What is in the menu bar.
public sealed partial class SkylineDebugger
{
    private UILayoutOverlayOptions? layoutOutlines;
    private bool physicsOutlines;

    private void BuildMenus()
    {
        Menu view = menus.AddMenu("View");
        view.Add("Scene tree", () => dock.ShowLeft = !dock.ShowLeft).IsChecked = () => dock.ShowLeft;
        view.Add("Inspector", () => dock.ShowRight = !dock.ShowRight).IsChecked = () => dock.ShowRight;
        view.Add("Drawer", () => dock.ShowBottom = !dock.ShowBottom).IsChecked = () => dock.ShowBottom;
        view.AddSeparator();
        view.Add("Content", () => OpenDrawer(0));
        view.Add("Metrics", () => OpenDrawer(1));
        view.Add("Log", () => OpenDrawer(2));
        view.AddSeparator();
        view.Add("Hide all of this", () => Shown = false, "F10");

        Menu game = menus.AddMenu("Game");
        game.Add("Pause", TogglePause, "F8").IsChecked = () => Engine.SceneManager.Paused;
        game.Add("Step one tick", StepOnce, "F9");
        game.AddSeparator();
        foreach (float speed in Speeds)
        {
            float chosen = speed;
            game.Add($"Speed {chosen:0%}", () =>
            {
                Engine.SceneManager.TimeScale = chosen;
                Say(chosen == 1.0f ? "the scene runs at its own speed" : $"the scene runs at {chosen:0%} of its speed");
            }).IsChecked = () => Engine.SceneManager.TimeScale == chosen;
        }
        game.AddSeparator();
        game.Add("Screenshot", () => Engine.CaptureScreenshot(Path.Combine(Engine.ScreenshotDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png")), "F12");
        game.Add("Quit", Engine.Exit);

        Menu scene = menus.AddMenu("Scene");
        scene.Add("Select the scene", () => Select(Engine.Scene)).IsEnabled = () => Engine.Scene is not null;
        scene.Add("Select its camera", () => Select(Engine.ActiveCamera));
        scene.Add("Select the engine", () => Select(Engine));
        scene.AddSeparator();
        scene.Add("Fold the tree away", () => tree.FoldAll());

        Menu show = menus.AddMenu("Show");
        show.Add("UI layout outlines", ToggleLayoutOutlines).IsChecked = () => layoutOutlines is not null;
        show.Add("Physics outlines", TogglePhysicsOutlines).IsChecked = () => physicsOutlines;
        show.AddSeparator();
        show.Add("Performance overlay", CyclePerformanceOverlay, "F3");
    }

    private void OpenDrawer(int page)
    {
        Shown = true;
        dock.ShowBottom = true;
        dock.Bottom.Selected = page;
    }

    private void ToggleLayoutOutlines()
    {
        layoutOutlines = layoutOutlines is null ? new UILayoutOverlayOptions() : null;

        int count = 0;
        Each<UICompositor>(compositor =>
        {
            compositor.LayoutOverlay = layoutOutlines;
            count++;
        });

        Say(layoutOutlines is null ? "no more outlines" : $"outlining the layout of {count} UIs, the ones there are right now");
    }

    private void TogglePhysicsOutlines()
    {
        physicsOutlines = !physicsOutlines;

        int count = 0;
        Each<PhysicsWorld>(world =>
        {
            world.RenderDebug = physicsOutlines;
            count++;
        });

        Say(count == 0 ? "there is no physics world in this scene" : physicsOutlines ? $"outlining the fixtures of {count} physics worlds" : "no more outlines");
    }

    private void CyclePerformanceOverlay()
    {
        PerformanceOverlay? overlay = null;
        Each<PerformanceOverlay>(found => overlay ??= found);

        if (overlay is null)
        {
            Say("nobody added a PerformanceOverlay to this game, engine.AddEntity(new PerformanceOverlay()) does");
            return;
        }

        overlay.Detail = (PerformanceDetail)(((int)overlay.Detail + 1) % 3);
    }
}
