using Horizon.Testing.Checks;
using Horizon.Testing.Examples.FirstSteps;
using Horizon.Testing.Examples.Game;
using Horizon.Testing.Examples.Internals;
using Horizon.Testing.Examples.Lighting;
using Horizon.Testing.Examples.Showcase;

namespace Horizon.Testing;

/// <summary>
/// Every example the selector offers, simplest first. The order in here is the order they are listed in and the
/// order Page Up and Page Down walk them in, and each one only uses what the ones above it have shown.
/// <para>
/// Adding one is two steps, write a scene for it in the folder of its level under Examples/ and give it a line
/// here. Nothing else needs to know it exists. If it reacts to keys or the mouse, implement
/// <see cref="ITestControls"/> and the host puts them on screen for you. Art and maps go in Assets/examples as
/// files, made by a script in Art/ if they are made at all, never painted as the scene starts.
/// </para>
/// </summary>
internal static class TestCatalog
{
    public static readonly TestDefinition[] Tests = Numbered(
    [
        /* First steps */

        new("quickstart", TestLevel.FirstSteps, "Quick start",
            "The least a scene can be. Scene2D hands over a lit world, a camera and a UI, and forty lines put a bouncing ball with a lamp on it in there.",
            ["Scene2D", "Light2D", "OcclusionMap2D", "Shapes"],
            "Examples/FirstSteps/QuickStartExample.cs", () => new QuickStartExample()),

        new("sprites", TestLevel.FirstSteps, "Sprites and animation",
            "A blob off a sprite sheet with named animations, scenery out of an atlas, and every trick a sprite can do, flipping, tints, origins, the ready made tweens.",
            ["SpriteBatch", "Sprite", "SpriteSheet", "TextureAtlas", "SpriteSheetDefinition", "SpriteTweens"],
            "Examples/FirstSteps/SpritesExample.cs", () => new SpritesExample()),

        new("shapes", TestLevel.FirstSteps, "Shapes",
            "Flat coloured shapes with no texture anywhere. Lines, boxes and discs in one draw call, debug overlays over bouncing balls, a bar graph, and a mesh of your own with a shader of your own.",
            ["PrimitiveRenderer", "ShapeList", "Mesh2D", "Technique"],
            "Examples/FirstSteps/ShapesExample.cs", () => new ShapesExample()) { Aliases = ["primitives"] },

        new("input", TestLevel.FirstSteps, "Keyboard and mouse",
            "Keys that light up while they are held, a ring chasing the pointer through the world, click ripples, the wheel, and a log of what happened on which tick.",
            ["InputManager", "Keyboard", "Mouse", "Camera.ScreenToWorld"],
            "Examples/FirstSteps/KeyboardMouseExample.cs", () => new KeyboardMouseExample()) { Aliases = ["keyboard-mouse"] },

        new("gamepads", TestLevel.FirstSteps, "Gamepads",
            "What is held on every pad that is plugged in, actions bound to buttons and combinations of them, a pad that is fed by hand, and the bindings saved out and read back.",
            ["Gamepads", "GamepadInput", "bindings", "virtual gamepads"],
            "Examples/FirstSteps/GamepadExample.cs", () => new GamepadExample()),

        new("camera", TestLevel.FirstSteps, "Cameras",
            "A camera chasing a little bloke round an island. Following, zooming, pixel snapping with and without an anchor, hard cuts, a minimap through a camera of its own.",
            ["Camera2D", "PixelSnap", "PixelSnapAnchor", "Camera.Snap", "Camera.Bounds", "SpriteBatch.CustomCamera"],
            "Examples/FirstSteps/CameraExample.cs", () => new CameraExample()),

        new("entities", TestLevel.FirstSteps, "Entities and threads",
            "The building blocks, live. Hives hatch bugs that fly about and get binned while the scene's own tree is printed next to them, with which thread runs what.",
            ["Entity", "GameObject", "GameComponent", "UpdateState", "UpdatePhysics", "Capture", "Render"],
            "Examples/FirstSteps/EntitiesExample.cs", () => new EntitiesExample()),

        /* Making a game */

        new("tweens", TestLevel.MakingAGame, "Tweens",
            "Every easing side by side on a track, and what a tween promises (delays, loops, sequences) checked as the scene starts.",
            ["Tween", "Easing", "TweenRunner", "sequences"],
            "Examples/Game/TweenExample.cs", () => new TweenExample()),

        new("transitions", TestLevel.MakingAGame, "Scenes and transitions",
            "A tour round three scenes with a different way of getting to each, fades, blurs, rot and hard cuts, with preloading and a scene that is kept between visits.",
            ["SceneManager", "FadeTransition", "BlurTransition", "RotTransition", "Preload", "Scene.Persistent"],
            "Examples/Game/TransitionsExample.cs", () => new TransitionsExample()),

        new("tilemap", TestLevel.MakingAGame, "Tile maps",
            "A street drawn in Tiled and loaded off its file. Layers that scroll at their own speeds, a layer in front, tiles turned every way and animated, objects read by their class, tiles put down and taken away.",
            ["TileMap", "TileMapLayer", "TileMapObject", "DispatchObjects", "BuildColliders", "parallax"],
            "Examples/Game/TileMapExample.cs", () => new TileMapExample()),

        new("particles", TestLevel.MakingAGame, "Particles",
            "The two particle simulators side by side, the CPU one on the left and the compute shader one on the right, set off with the mouse.",
            ["ParticleRenderer2D", "CpuParticleSimulator2D", "ComputeParticleSimulator2D"],
            "Examples/Game/ParticleExample.cs", () => new ParticleExample()),

        new("fluid", TestLevel.MakingAGame, "Physics and fluid",
            "Water and sand poured into basins that spill into one another, a crate to drop in, and rain that lands on all of it.",
            ["PhysicsWorld", "PhysicsFluidParticleSimulator2D", "PhysicsParticleSimulator2D", "fixtures"],
            "Examples/Game/FluidExample.cs", () => new FluidExample()),

        new("ui", TestLevel.MakingAGame, "A HUD and a panel",
            "The first UI. Bars pinned to the corners of the screen, a panel put together in C# and the same panel written as a script, and telling the pointer being over the UI from it being over the game.",
            ["UICompositor", "UIModule", "StackPanel", "Button", "ProgressBar", "HIDL"],
            "Examples/Game/UIBasicsExample.cs", () => new UIBasicsExample()),

        new("ui-layouts", TestLevel.MakingAGame, "Menus out of layout files",
            "A settings screen that lives in a .hor file. Made for 16:9 and keeping its shape on any screen, pages you flip between, a list that keeps changing, right click menus and a HUD that moves as one.",
            ["UILayout", "compositor.design", "TabPanel", "Group", "UILayout.Bind", "ContextMenu", "PerformanceOverlay"],
            "Examples/Game/UILayoutsExample.cs", () => new UILayoutsExample()) { Aliases = ["ui-screens", "ui-layout"] },

        new("ui-controls", TestLevel.MakingAGame, "Every control",
            "One of everything UIX has, a page at a time. Dropdowns, sliders, the colour picker, lists and grids, text boxes and the on screen keyboards, dialogs and tooltips, all of it walkable with the arrow keys or a pad.",
            ["Dropdown", "Slider", "ColorPicker", "ListBox", "ScrollPanel", "TextBox", "OnScreenKeyboard", "UIDialog", "UINavigator"],
            "Examples/Game/UIControlsExample.cs", () => new UIControlsExample()) { Aliases = ["ui-navigation", "keyboard"] },

        new("ui-skin", TestLevel.MakingAGame, "Skins and themes",
            "The Dead Revolver pack as a skin, the same controls in every colour it comes in, icons in the middle of text, and sprites of the same pack drifting behind out of an atlas of their own.",
            ["UISkin", "themes", "icons in text", "TextureAtlas"],
            "Examples/Game/UISkinExample.cs", () => new UISkinExample()) { Aliases = ["ui-pack"] },

        /* Light and effects */

        new("lighting", TestLevel.LightAndEffects, "Lights and shadows",
            "A brick cellar out of a map, lit by the lamps the map puts in it. Point lights, a spot on a rope, a moon, shadows off the walls and off a sprite, normal and specular maps, ambient occlusion.",
            ["DeferredRenderer2D", "Light2D", "OcclusionMap2D", "TileMap.CreateOcclusion", "AmbientOcclusion2D", "normal maps"],
            "Examples/Lighting/LightingExample.cs", () => new LightingExample()) { Aliases = ["renderer2d"] },

        new("pathtraced", TestLevel.LightAndEffects, "Path traced lighting",
            "The same cellar with the fancy lighting on. Light bounces off the bricks and spills round the pillars, anything that glows is a lamp, and the tracer's own buffer is a key away.",
            ["LightingMode.PathTraced", "PathTracedLighting2D", "radiance cascades", "Sprite.FlashLights"],
            "Examples/Lighting/LightingExample.cs", () => new LightingExample(pathTraced: true)) { Aliases = ["pathtraced-buffer", "pathtraced-pan"] },

        new("post", TestLevel.LightAndEffects, "Post processing",
            "The street behind the glass of an old telly. Effects stacked on a renderer, a world rendered small and blown up, and a HUD that goes through the tube with the rest.",
            ["PostProcessing", "CrtEffect", "BlurEffect", "Renderer2D inside Renderer2D"],
            "Examples/Lighting/PostProcessExample.cs", () => new PostProcessExample()),

        /* All together */

        new("town", TestLevel.AllTogether, "A night in town",
            "Everything above in one scene, the way a game has it. A map with its lamps and colliders, a blob you walk and jump about it with some weight to him, lit and shadowed, rain that lands on him, the tube over the street and a HUD out of a layout file in front of it.",
            ["TileMap", "DeferredRenderer2D", "PhysicsWorld", "CharacterController2D", "ParticleRenderer2D", "UILayout", "PostProcessing"],
            "Examples/Showcase/TownExample.cs", () => new TownExample()),

        new("dungeon", TestLevel.AllTogether, "Down in the dark",
            "A dungeon from above, and a game. Three crystals, a gate that wants them and slimes in the way, lit by whatever in it glows, the lava, the mushrooms, the crystals he carries and the sparks he throws, with everything that moves a body in one physics world.",
            ["LightingMode.PathTraced", "Emissive", "FlashLights", "CharacterController2D", "PhysicsWorld.BodiesPush", "ApplyRadialImpulse", "PhysicsParticleSimulator2D", "TileMap"],
            "Examples/Showcase/DungeonExample.cs", () => new DungeonExample()) { Aliases = ["crawler"] },

        /* Under the hood */

        new("pacing", TestLevel.UnderTheHood, "Frame pacing",
            "Things moving at a steady speed, and a meter of how evenly they actually move from one frame to the next. A judder you can hardly see is a number here.",
            ["SnapshotClock", "interpolation", "RenderFrame", "VSync"],
            "Examples/Internals/PacingExample.cs", () => new PacingExample()),

        /* Checks */

        new("check-tilemap", TestLevel.Checks, "Tile maps",
            "Loads three small maps that between them have one of everything a map can have, and checks that what comes out is what was put in.",
            ["TileMap"],
            "Checks/TileMapChecks.cs", () => new TileMapChecks()),

        new("check-ui", TestLevel.Checks, "UI, the basics",
            "A scripted pointer clicks through buttons, sliders and toggles made in C# and by a script.",
            ["UICompositor", "HIDL"],
            "Checks/UIBasicsCheck.cs", () => new UIBasicsCheck()) { Aliases = ["ui-selftest"] },

        new("check-ui-layout", TestLevel.Checks, "UI, layout",
            "Grids, scaling a whole UI, scrolling, number boxes, and layouts loaded from files with their items made from templates.",
            ["UILayout", "GridPanel", "ScrollPanel"],
            "Checks/UILayoutCheck.cs", () => new UILayoutCheck()) { Aliases = ["ui-layout-selftest"] },

        new("check-ui-controls", TestLevel.Checks, "UI, controls",
            "The dropdown and its list, sliders with ends and steps, the colour picker, and the entrances a layout gives its parts.",
            ["Dropdown", "Slider", "ColorPicker"],
            "Checks/UIControlsCheck.cs", () => new UIControlsCheck()) { Aliases = ["ui-controls-selftest"] },

        new("check-ui-navigation", TestLevel.Checks, "UI, navigation",
            "The navigator driven from code. Walking menus and grids, list boxes, tabbing and pasting, tooltips, dialogs and what is on top of what.",
            ["UINavigator", "UIDialog", "ListBox"],
            "Checks/UINavigationCheck.cs", () => new UINavigationCheck()) { Aliases = ["ui-navigation-selftest"] },
    ]);

    /// <summary>Finds an example by its id, or by one of the ids the examples before it went by, whatever the case. Null if there's no such thing.</summary>
    public static TestDefinition? Find(string id) =>
        Tests.FirstOrDefault(test => test.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? Tests.FirstOrDefault(test => test.Aliases.Contains(id, StringComparer.OrdinalIgnoreCase));

    /// <summary>What a level is called on screen.</summary>
    public static string NameOf(TestLevel level) => level switch
    {
        TestLevel.FirstSteps => "First steps",
        TestLevel.MakingAGame => "Making a game",
        TestLevel.LightAndEffects => "Light and effects",
        TestLevel.AllTogether => "All together",
        TestLevel.UnderTheHood => "Under the hood",
        _ => "Checks"
    };

    /// <summary>What a level is about, in a line under its name.</summary>
    public static string BlurbOf(TestLevel level) => level switch
    {
        TestLevel.FirstSteps => "A window, something drawn in it, something read off the keyboard.",
        TestLevel.MakingAGame => "Maps, scenes, particles, physics and menus.",
        TestLevel.LightAndEffects => "Lamps, shadows, bounced light and the glass over the picture.",
        TestLevel.AllTogether => "The lot in one scene, the way a game has it.",
        TestLevel.UnderTheHood => "Why things move the way they do.",
        _ => "Not lessons. They drive themselves and say whether the engine still works."
    };

    /// <summary>Helper method to number the examples in the order they are written down, the checks aside.</summary>
    private static TestDefinition[] Numbered(TestDefinition[] tests)
    {
        int number = 0;
        return [.. tests.Select(test => test.Level == TestLevel.Checks ? test : test with { Number = ++number })];
    }
}
