using Horizon.Testing.Scenes;

namespace Horizon.Testing;

/// <summary>
/// Every test the selector offers, in the order it lists them.
/// To add a test, write a scene for it and give it a line here; nothing else needs to know about it.
/// If the scene reacts to keys or the mouse, have it implement <see cref="ITestControls"/> and the
/// host lists them on screen while it runs.
/// </summary>
internal static class TestCatalog
{
    public static readonly TestDefinition[] Tests =
    [
        new("ui", "User interface", "Layout, skinning, pointer input and HIDL scripting.", () => new UITestScene()),
        new("keyboard", "On Screen Keyboard", "Number and Alphanumeric on screen keyboards.", () => new OnScreenKeyboardTestScene()),
        new("ui-selftest", "UI self-test", "The UI test clicking through itself and checking the results.", () => new UITestScene(selfTest: true)),
        new("ui-layout", "UI layout", "Scaling a UI to its window, grids, scrolling and UIs out of layout files.", () => new UILayoutTestScene()),
        new("ui-layout-selftest", "UI layout self-test", "The UI layout test clicking through itself and checking the results.", () => new UILayoutTestScene(selfTest: true)),
        new("ui-controls", "UI controls", "Dropdowns, sliders with ends and steps, the colour picker and entrances out of a layout.", () => new UIControlsTestScene()),
        new("ui-controls-selftest", "UI controls self-test", "The UI controls test clicking through itself and checking the results.", () => new UIControlsTestScene(selfTest: true)),
        new("ui-pack", "UI pack", "The Dead Revolver skin: its themes, one atlas and icons in text.", () => new UIPackTestScene()),
        new("tweens", "Tweens", "Every easing side by side, and what a tween promises checked as the scene starts.", () => new TweenTestScene()),
        new("gamepads", "Gamepads", "What is held on every gamepad, bindings, combinations and saving them as HIDL.", () => new GamepadTestScene()),
        new("tilemap", "Tile maps", "A Tiled map with one of everything: turned and animated tiles, parallax, groups, objects and templates.", () => new TileMapTestScene()),
        new("post", "Post processing", "A lit world with its motion blurred, and a HUD, behind the glass of a picture tube.", () => new PostProcessTestScene()),
        new("particles", "Particles", "CPU and compute shader simulation side by side.", () => new ParticleTestScene()),
        new("fluid", "Fluid particles", "Physics particles that stack, level out and overflow from basin to basin.", () => new FluidTestScene()),
        new("lighting", "Lighting", "Deferred lights, shadows, normal maps and emissive particles.", () => new LightingTestScene()),
        new("renderer2d", "Renderer2D", "The same scene unlit, drawn at a quarter of the size and blown up.", () => new LightingTestScene(deferred: false)),
        new("pacing", "Frame pacing", "Steady movers and a meter of how evenly they move from frame to frame.", () => new PacingTestScene()),
    ];

    public static TestDefinition? Find(string id) =>
        Tests.FirstOrDefault(test => test.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
