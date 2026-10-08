using Horizon.Testing.Examples.Basics;
using Horizon.Testing.Examples.Engine;
using Horizon.Testing.Examples.Input;
using Horizon.Testing.Examples.Physics;
using Horizon.Testing.Examples.Rendering;
using Horizon.Testing.Examples.UI;

namespace Horizon.Testing;

/// <summary>
/// Every test the selector offers. They're listed under the heading of their <see cref="TestArea"/>, in the order
/// they're in here.
/// <para>
/// Adding one is two steps: write a scene for it in Examples/&lt;Area&gt;/ and give it a line here. That's it,
/// nothing else needs to know it exists. If it reacts to keys or the mouse, implement <see cref="ITestControls"/>
/// and the host puts them on screen for you.
/// </para>
/// </summary>
internal static class TestCatalog
{
    public static readonly TestDefinition[] Tests =
    [
        // Basics: start here if you're new. Entities, sprites and cameras, the stuff every game is made of.
        new("quickstart", TestArea.Basics, "Quick start", "The least a scene can be, Scene2D with a lit world, a bouncing ball with a lamp on it, a couple of walls and a label, in forty lines.", () => new QuickStartExample()),
        new("entities", TestArea.Basics, "Entities and components", "The building blocks: entities, components, which thread does what, and the scene's tree, live.", () => new EntitiesExample()),
        new("sprites", TestArea.Basics, "Sprites and animation", "Sprite sheets with named animations, flipping, tints, origins, sprite tweens and sprites out of an atlas.", () => new SpritesExample()),
        new("camera", TestArea.Basics, "Cameras", "Following, zooming, pixel snapping with and without an anchor, hard cuts, and what's on screen.", () => new CameraExample()),
        new("transitions", TestArea.Basics, "Scenes and transitions", "Hopping between scenes with fades, blurs, rot and hard cuts, preloading and a scene that's kept.", () => new TransitionsExample()),

        // Input: what the player is pressing, on whatever they're pressing it with.
        new("keyboard-mouse", TestArea.Input, "Keyboard and mouse", "Keys that light up, a ring chasing the pointer, click ripples, the wheel and a log of every press.", () => new KeyboardMouseExample()),
        new("gamepads", TestArea.Input, "Gamepads", "What's held on every pad, bindings, combos and saving them out as HIDL.", () => new GamepadExample()),

        // Rendering: everything that ends up as pixels that isn't a UI.
        new("primitives", TestArea.Rendering, "Shapes", "Triangles, rectangles and circles in one draw call: lines, debug overlays, a bar graph and a Mesh2D star.", () => new PrimitivesExample()),
        new("tilemap", TestArea.Rendering, "Tile maps", "A Tiled map with one of everything: turned and animated tiles, parallax, groups, objects and templates.", () => new TileMapExample()),
        new("post", TestArea.Rendering, "Post processing", "A lit world and a HUD behind the glass of an old telly.", () => new PostProcessExample()),
        new("particles", TestArea.Rendering, "Particles", "CPU and compute shader particles, side by side.", () => new ParticleExample()),
        new("lighting", TestArea.Rendering, "Lighting", "Deferred lights, shadows, normal maps and particles that glow.", () => new LightingExample()),
        new("renderer2d", TestArea.Rendering, "Renderer2D", "The lighting scene unlit, drawn at a quarter of the size and blown up.", () => new LightingExample(deferred: false)),
        new("pathtraced", TestArea.Rendering, "Path traced lighting", "The lighting scene with the fancy lighting on, light bouncing off the blocks and spilling round them.", () => new LightingExample(pathTraced: true)),
        new("pathtraced-pan", TestArea.Rendering, "Path tracer under a panning camera", "The path tracer's buffer with nothing moving but the camera, for catching the lighting changing its mind from frame to frame.", () => new LightingExample(pathTraced: true, showTraced: true, pan: true)),
        new("pathtraced-buffer", TestArea.Rendering, "Path tracer's buffer", "What the path tracer found, on its own, for seeing what it is up to.", () => new LightingExample(pathTraced: true, showTraced: true)),

        // UI: UIX, in C# and out of layout files. The self-tests drive the pointer themselves and check the results,
        // so run them after you've been mucking about in UIX.
        new("ui", TestArea.UI, "User interface", "Layout, skinning, the pointer and HIDL scripting.", () => new UIExample()),
        new("keyboard", TestArea.UI, "On screen keyboard", "Number and alphanumeric on screen keyboards.", () => new OnScreenKeyboardExample()),
        new("ui-selftest", TestArea.UI, "UI self-test", "The UI test clicking through itself and checking the results.", () => new UIExample(selfTest: true)),
        new("ui-layout", TestArea.UI, "UI layout", "Scaling a UI to its window, grids, scrolling and UIs out of layout files.", () => new UILayoutExample()),
        new("ui-layout-selftest", TestArea.UI, "UI layout self-test", "The UI layout test clicking through itself and checking the results.", () => new UILayoutExample(selfTest: true)),
        new("ui-controls", TestArea.UI, "UI controls", "Dropdowns, sliders with ends and steps, the colour picker and entrances out of a layout.", () => new UIControlsExample()),
        new("ui-controls-selftest", TestArea.UI, "UI controls self-test", "The UI controls test clicking through itself and checking the results.", () => new UIControlsExample(selfTest: true)),
        new("ui-screens", TestArea.UI, "UI screens", "A menu made for 16:9 that keeps its shape on any screen, tabs, groups, right click menus, lists that keep changing and the performance overlay.", () => new UIScreensExample()),
        new("ui-navigation", TestArea.UI, "UI navigation", "Walking a menu with a gamepad or the arrow keys, labels that wrap, tooltips, a dialog, tab and paste.", () => new UINavigationExample()),
        new("ui-navigation-selftest", TestArea.UI, "UI navigation self-test", "The navigation test driving itself and checking the results.", () => new UINavigationExample(selfTest: true)),
        new("ui-pack", TestArea.UI, "UI pack", "The Dead Revolver skin: its themes, one atlas and icons in text.", () => new UIPackExample()),

        // Physics: bodies, and particles that behave like fluid.
        new("fluid", TestArea.Physics, "Fluid particles", "Physics particles that stack, level out and spill from basin to basin.", () => new FluidExample()),

        // Engine: the guts. How the threads tick, how smooth frames come out, and the tweens everything animates with.
        new("tweens", TestArea.Engine, "Tweens", "Every easing side by side, plus what a tween promises, checked as it starts.", () => new TweenExample()),
        new("pacing", TestArea.Engine, "Frame pacing", "Steady movers and a meter of how evenly they move from frame to frame.", () => new PacingExample()),
    ];

    /// <summary>Finds a test by its id, whatever the case. Null if there's no such test.</summary>
    public static TestDefinition? Find(string id) =>
        Tests.FirstOrDefault(test => test.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
