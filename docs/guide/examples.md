# The examples, one by one

Every example in the selector, in the order the selector lists them, which is also the order Page Up and Page Down walk them in, each one only uses what the ones above it have shown. The id is what `dotnet run -- <id>` takes, the sources are under `Horizon.Testing/`, and the names in the last column are the parts of the engine the example is about, by the names they go by in code, so they can be searched for.

## First steps

A window, something drawn in it, something read off the keyboard.

| Id | Example | What there is to see | Shows |
|---|---|---|---|
| `quickstart` | Quick start | The least a scene can be, `Scene2D` hands over a lit world, a camera and a UI, and forty lines put a bouncing ball with a lamp on it in there. `Examples/FirstSteps/QuickStartExample.cs` | `Scene2D`, `Light2D`, `OcclusionMap2D`, `Shapes` |
| `sprites` | Sprites and animation | A blob off a sprite sheet with named animations, scenery out of an atlas, and every trick a sprite can do. `Examples/FirstSteps/SpritesExample.cs` | `SpriteBatch`, `Sprite`, `SpriteSheet`, `TextureAtlas`, `SpriteSheetDefinition`, `SpriteTweens` |
| `shapes` | Shapes | Lines, boxes and discs in one draw call, debug overlays, a bar graph, and a mesh with a shader of its own. `Examples/FirstSteps/ShapesExample.cs` | `PrimitiveRenderer`, `ShapeList`, `Mesh2D`, `Technique` |
| `input` | Keyboard and mouse | Keys that light up while held, a ring chasing the pointer, click ripples, the wheel, and a log of what happened on which tick. `Examples/FirstSteps/KeyboardMouseExample.cs` | `InputManager`, `Keyboard`, `Mouse`, `Camera.ScreenToWorld` |
| `gamepads` | Gamepads | What is held on every pad, actions bound to buttons and combinations, a pad fed by hand, and the bindings saved out and read back. `Examples/FirstSteps/GamepadExample.cs` | `Gamepads`, `GamepadInput`, bindings, virtual gamepads |
| `camera` | Cameras | A camera chasing a little bloke round an island, following, zooming, pixel snapping, hard cuts and a minimap. `Examples/FirstSteps/CameraExample.cs` | `Camera2D`, `PixelSnap`, `PixelSnapAnchor`, `Camera.Snap`, `Camera.Bounds`, `SpriteBatch.CustomCamera` |
| `entities` | Entities and threads | Hives hatch bugs that fly about and get binned while the scene's own tree is printed next to them, with which thread runs what. `Examples/FirstSteps/EntitiesExample.cs` | `Entity`, `GameObject`, `GameComponent`, `UpdateState`, `UpdatePhysics`, `Capture`, `Render` |

## Making a game

Maps, scenes, particles, physics and menus.

| Id | Example | What there is to see | Shows |
|---|---|---|---|
| `tweens` | Tweens | Every easing side by side on a track, and what a tween promises checked as the scene starts. `Examples/Game/TweenExample.cs` | `Tween`, `Easing`, `TweenRunner`, sequences |
| `transitions` | Scenes and transitions | A tour round three scenes, fades, blurs, rot and hard cuts, with preloading and a scene kept between visits. `Examples/Game/TransitionsExample.cs` | `SceneManager`, `FadeTransition`, `BlurTransition`, `RotTransition`, `Preload`, `Scene.Persistent` |
| `tilemap` | Tile maps | A street drawn in Tiled and loaded off its file, parallax layers, animated tiles, objects read by their class, tiles put down and taken away. `Examples/Game/TileMapExample.cs` | `TileMap`, `TileMapLayer`, `TileMapObject`, `DispatchObjects`, `BuildColliders` |
| `particles` | Particles | The CPU particle simulator and the compute shader one side by side, set off with the mouse. `Examples/Game/ParticleExample.cs` | `ParticleRenderer2D`, `CpuParticleSimulator2D`, `ComputeParticleSimulator2D` |
| `meshes` | A bit of 3D | A floor, a crate, a ball, a can and hills built from vertices by hand, lit by one sun, through a camera that goes round them. `Examples/Game/MeshesExample.cs` | `Renderer3D`, `Mesh3D`, `Vertex3D`, `Camera3D`, `TransformComponent3D`, `CullMode` |
| `fluid` | Physics and fluid | Water and sand poured into basins that spill into one another, a crate to drop in, and rain on all of it. `Examples/Game/FluidExample.cs` | `PhysicsWorld`, `PhysicsFluidParticleSimulator2D`, `PhysicsParticleSimulator2D`, fixtures |
| `ui` | A HUD and a panel | Bars pinned to the corners, a panel built in C# and the same panel as a script. `Examples/Game/UIBasicsExample.cs` | `UICompositor`, `UIModule`, `StackPanel`, `Button`, `ProgressBar`, HIDL |
| `ui-layouts` | Menus out of layout files | A settings screen in a `.hor` file that keeps its shape on any screen, pages, a changing list, right click menus. `Examples/Game/UILayoutsExample.cs` | `UILayout`, `compositor.design`, `TabPanel`, `Group`, `UILayout.Bind`, `ContextMenu` |
| `ui-controls` | Every control | One of everything UIX has, a page at a time, walkable with the arrow keys or a pad. `Examples/Game/UIControlsExample.cs` | `Dropdown`, `Slider`, `ColorPicker`, `ListBox`, `TextBox`, `UIDialog`, `UINavigator` |
| `ui-skin` | Skins and themes | The Dead Revolver pack as a skin in every colour it comes in, and icons in the middle of text. `Examples/Game/UISkinExample.cs` | `UISkin`, themes, icons in text, `TextureAtlas` |
| `touch-input` | Touch, mouse and keyboard | Everything that points and types on one page that says what it heard. `Examples/Game/TouchInputExample.cs` | `Touch`, `Mouse`, `Keyboard`, `UIPointer`, `PointerKind`, `ScrollPanel` |

## Light and effects

Lamps, shadows, bounced light and the glass over the picture.

| Id | Example | What there is to see | Shows |
|---|---|---|---|
| `lighting` | Lights and shadows | A brick cellar out of a map lit by the lamps the map puts in it, point lights, a spot on a rope, a moon, shadows, normal and specular maps, ambient occlusion. `Examples/Lighting/LightingExample.cs` | `DeferredRenderer2D`, `Light2D`, `OcclusionMap2D`, `TileMap.CreateOcclusion`, `AmbientOcclusion2D` |
| `pathtraced` | Path traced lighting | The same cellar with the path traced lighting on, light bouncing off the bricks, and anything that glows a lamp. `Examples/Lighting/LightingExample.cs` | `LightingMode.PathTraced`, `PathTracedLighting2D`, radiance cascades, `Sprite.FlashLights` |
| `post` | Post processing | The street behind the glass of an old telly, effects stacked on a renderer and a HUD that goes through the tube with the rest. `Examples/Lighting/PostProcessExample.cs` | `PostProcessing`, `CrtEffect`, `BlurEffect` |

## All together

The lot in one scene, the way a game has it.

| Id | Example | What there is to see | Shows |
|---|---|---|---|
| `town` | A night in town | A map with its lamps and colliders, a blob to walk and jump about, rain that lands on him, the tube over the street and a HUD out of a layout file. `Examples/Showcase/TownExample.cs` | `TileMap`, `DeferredRenderer2D`, `PhysicsWorld`, `CharacterController2D`, `UILayout`, `BloomEffect` |
| `dungeon` | Down in the dark | A dungeon from above and a game, three crystals, a gate that wants them, slimes in the way, lit by whatever in it glows. `Examples/Showcase/DungeonExample.cs` | `LightingMode.PathTraced`, `Emissive`, `FlashLights`, `CharacterController2D`, `ApplyRadialImpulse` |
| `village` | A day in the village | A town from above with doors to walk through, every inside a map of its own faded up as you step in, a sun that goes round and villagers with something to say. `Examples/Showcase/VillageExample.cs` | `TileMapRooms`, `TileMapDoor`, `LightType.Directional`, `ExposureEffect`, `TileMapPathfinder` |

The village's art is two packs off itch.io, Tiny RPG Town by ansimuz (CC0) and AxulArt's interior (CC BY 4.0).

## Under the hood

Why things move the way they do.

| Id | Example | What there is to see | Shows |
|---|---|---|---|
| `pacing` | Frame pacing | Things moving at a steady speed and a meter of how evenly they move from one frame to the next, a judder that can hardly be seen is a number here. `Examples/Internals/PacingExample.cs` | `SnapshotClock`, interpolation, `RenderFrame`, `VSync` |
| `terminal` | A terminal | The machine's shell in a `TerminalView`, ConPTY and a VT screen, vim included. Not on every platform, the selector says why where it is not. `Examples/Internals/TerminalExample.cs` | `TerminalView`, `TerminalScreen`, `PseudoTerminal` |

The checks come after these in the selector, they are not lessons and are covered on their own page, [Checks](checks.md).
