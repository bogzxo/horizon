using Horizon.Testing.Examples.Engine;
using Horizon.Testing.Examples.Input;
using Horizon.Testing.Examples.Physics;
using Horizon.Testing.Examples.Rendering;
using Horizon.Testing.Examples.UI;

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
        new("ui", "User interface", "Layout, skinning, pointer input and HIDL scripting.", () => new UIExample()),
        new("keyboard", "On Screen Keyboard", "Number and Alphanumeric on screen keyboards.", () => new OnScreenKeyboardExample()),
        new("ui-selftest", "UI self-test", "The UI test clicking through itself and checking the results.", () => new UIExample(selfTest: true)),
        new("ui-layout", "UI layout", "Scaling a UI to its window, grids, scrolling and UIs out of layout files.", () => new UILayoutExample()),
        new("ui-layout-selftest", "UI layout self-test", "The UI layout test clicking through itself and checking the results.", () => new UILayoutExample(selfTest: true)),
        new("ui-controls", "UI controls", "Dropdowns, sliders with ends and steps, the colour picker and entrances out of a layout.", () => new UIControlsExample()),
        new("ui-controls-selftest", "UI controls self-test", "The UI controls test clicking through itself and checking the results.", () => new UIControlsExample(selfTest: true)),
        new("ui-pack", "UI pack", "The Dead Revolver skin: its themes, one atlas and icons in text.", () => new UIPackExample()),
        new("tweens", "Tweens", "Every easing side by side, and what a tween promises checked as the scene starts.", () => new TweenExample()),
        new("gamepads", "Gamepads", "What is held on every gamepad, bindings, combinations and saving them as HIDL.", () => new GamepadExample()),
        new("tilemap", "Tile maps", "A Tiled map with one of everything: turned and animated tiles, parallax, groups, objects and templates.", () => new TileMapExample()),
        new("post", "Post processing", "A lit world with its motion blurred, and a HUD, behind the glass of a picture tube.", () => new PostProcessExample()),
        new("particles", "Particles", "CPU and compute shader simulation side by side.", () => new ParticleExample()),
        new("fluid", "Fluid particles", "Physics particles that stack, level out and overflow from basin to basin.", () => new FluidExample()),
        new("lighting", "Lighting", "Deferred lights, shadows, normal maps and emissive particles.", () => new LightingExample()),
        new("renderer2d", "Renderer2D", "The same scene unlit, drawn at a quarter of the size and blown up.", () => new LightingExample(deferred: false)),
        new("pacing", "Frame pacing", "Steady movers and a meter of how evenly they move from frame to frame.", () => new PacingExample()),
    ];

    public static TestDefinition? Find(string id) =>
        Tests.FirstOrDefault(test => test.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
