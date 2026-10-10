# Working on Horizon

Notes from bogz for whoever (or whatever) writes code and commits in here.

## Voice

Comments and commit messages read like the early development commits, not like a press release. Casual, a bit
intellectual, the odd swear word where it earns its keep ("fuck it we ball", "a blurry pile of shit", "galactus
allocation", "no nullptr c#!!!! woww!!!!"). Short commit titles that say what happened ("Horizon.Physics now works!",
"so theres an interpreted language now", "Removed vestigial projects... :("). Explain the why in plain sentences,
lowercase inline comments are fine, exclamation marks are fine, a joke is fine.

Avoid the AI sounding colon construction ("Thing: explanation", "Rendering: ...") in comments, docs and commit
messages. Write a sentence instead. No bullet lists of "Added X" in commit bodies either, say it like a person.

## How the rendering is done

Horizon draws with Vulkan and nothing else, through Silk.NET. There is no abstraction over the backend any more,
`Horizon.Graphics` is the device (`GraphicsDevice`, in parts, with the Vulkan plumbing in `Graphics/Vulkan`) and the
resource classes (`Texture`, `GpuBuffer`, `RenderTarget`, `Shader`, `Technique`, `StreamBuffer`), and renderers
talk to those. Anything that needs a Vulkan call the device hasn't got gets the device extended, not `Vk.`
sprinkled into a renderer. The beginner's way in is still `Technique` (load a shader, set uniforms by name, bind,
draw) and the renderers, the device underneath keeps two frames in flight on a timeline semaphore, hands out uniforms
and staging out of per frame arenas, builds pipelines as it meets them and keeps them on disk.

The conventions every shader goes by. Set 0 is the buffers, the camera block at binding 0 and the Params block at 1
(both on dynamic offsets, `Technique.SetUniform` writes Params by the names reflection found), storage blocks at 2 to
9. Set 1 is textures 0 to 7 (`BIND_TEXTURE`), storage images 8 to 11 (`BIND_IMAGE`) and samplers 12 to 19 for HLSL.
Set 2 is the bindless table, four thousand samplers, which is how a sprite names its texture (`Texture.BindlessIndex`)
and a whole batch is one draw. Matrices are row major and multiplied the System.Numerics way, `mul(v, M)`, and the
projection is already turned the way Vulkan wants its clip space (`GraphicsDevice.ClipCorrection`).

Shaders are Slang (`.slang`, shader-slang.org), one file with every stage in it (`vsMain`, `fsMain`, `csMain`), with
`#include <common/camera.slang>` and friends, compiled at run time by Slangc and cached under LocalAppData by their
hash. HLSL (`.hlsl`, the same entry point names) compiles through the same path. There is no GLSL.

There is no motion blur and nothing keeps track of how fast anything moves on screen, that went, the G-buffer is
albedo, surface and material and that is all.

Where that paid off is written into the code. Sprites and the UI are vertexless quads out of a storage block, tile
maps live on the GPU as chunks a compute pass culls into one indirect draw, particles are simulated and compacted on
the GPU and drawn indirect, lights are sorted into screen tiles, the occlusion map is a signed distance field the
shadows march, sprites that cast shadows do so through a distance field of the picture built every frame, and the
path traced lighting is radiance cascades in compute. The performance overlay (`UI/Diagnostics/PerformanceOverlay`,
F3) is a pill with the frame rate in it or, once more, a card with the last twelve seconds of frames, the threads,
what every pass of a frame costs the GPU as a bar, what the frame asked of the device, the memory and the garbage.
It is one component that paints itself (`PerformanceBoard`) out of what was last read (`PerformanceSample`), and
it makes no garbage doing it, numbers are written into the same few characters (`PerformanceInk`) and never into
a string. Every loop keeps a timeline for it, a slice a tenth of a second (`LoopStatistics.CopyTimeline`).

## Read the notebook

`docs/notebook.md` is where whoever worked in here last left what they learned, what they were up to and hints to
themselves. Read it before crawling the tree, add to it as you go.

## Lights

`Light2D` is a point light unless its `Type` says spot (a cone, `Direction`, `ConeAngle`, `ConeSoftness`, turn it
by setting its direction every frame) or directional (the same everywhere, the moon, `Direction` and `Reach`).
`SpriteShadow` is how much of a light the sprites that block light take, 1 like a wall, less and a fighter throws a
shadow without putting the lantern out, and a sprite the light itself is inside of (a fighter standing in front of
the lamp) throws no shadow from it at all, the march notices it never leaves the sprite before it gets to the light.
The lighting passes of a frame, in order, are the lights to the GPU, the sprite shadow field (a jump flood of what blocks or glows, only when something casts and a
light is in view), the light tiles (only with lights), the lighting per lighting pixel (`LightCells`, only with a
`LightingPixelSize`), the path tracer (the wall radiance once, then the cascades, then the resolve), and the
deferred pass that puts it on screen. `HORIZON_LOG_LOOPS` prints what each of those cost the GPU.

A tile map says what blocks light with two layer properties. `CastsShadows` on a tile layer puts its tiles in the
occlusion map, `BlocksLight` on an object layer puts the box of every object on it in there (a pillar or a crate
put down as an object), and a collidable layer casts nothing unless it says so, the colliders of a map are hidden
and not what the light should see. `TileMap.ShadowCasters()` is the lot of it by the cell, and
`TileMap.CreateOcclusion()` makes the occlusion map by the shape of the art instead (a pot is not a square, the air
in the corners of its tile is air), which is the one a game wants.

The body of anything that blocks light (the ground under the fighters) is drawn and lit as its face, and it stops
every shadow at its edge, nothing runs on down through it (`DeferredRenderer2D.ShadowsInsideWalls` puts the wall
shadows back on it, the sprites never shadow it). The ambient occlusion (`DeferredRenderer2D.AmbientOcclusion`,
an `AmbientOcclusion2D`, off unless asked) darkens the corners, the ground under a fighter's feet and the air next
to a pillar, by marching a few short rays through the same distance field in the deferred pass, with the `_ao` maps
of the tile sets taken into account, packed under the sprite coverage in the green of the material attachment. It is not part of
the path tracing, the cascades see the world at probe spacing and this is contact scale, so it is there in both
lighting modes, and `Show` puts it on screen on its own (O and V in the lighting example). A pixel that is a
sprite casting a shadow doesn't count the sprites, or it is hemmed in by itself, and what a foot does to the floor
fades out on the way down into it. With its `Strength` at 0 nothing is marched and the maps still count.
`TileMap.GeometryOcclusion` (off unless asked, a map property of the same name works too) is the map doing the
corners itself, one picture of how hemmed in every spot is by what blocks light, made on the CPU when the map
changes and read by every layer that isn't the geometry, into the same green of the material. It costs a frame
next to nothing where the march costs a few tenths of a millisecond. `TileMap.OcclusionMapStrength` is how much
the painted maps count on a map, apart from its geometry, and the renderer's `BakedStrength` scales the two together.

## Text

Fonts are signed distance fields (`Rendering/Text/DistanceFieldFont.cs`), one R8 atlas a skin's text is drawn out
of at any size, crisp, no mipmaps. A skin names a TrueType file (`font: { dir, file, size }`, the size being pixels
to the em the metrics come out at, 96 like the bitmap fonts were baked) and stb_truetype draws the field, kerning
and all, or a BMFont `.fnt` and the picture's coverage is turned into a field at load. Built fonts are cached next
to the shaders. Cascadia Mono ships as its `.ttf` (OFL), Klaxon and VCR as the bitmaps they always were.

## Running on a Mac

Vulkan on a Mac is MoltenVK, which comes with the engine (Silk.NET.MoltenVK.Native, next to the exe). GLFW has no
Vulkan to offer on a Mac, so the window there is SDL's (everywhere else GLFW's), told where MoltenVK is through
SDL_VULKAN_LIBRARY, or it finds the Vulkan SDK's loader by itself when that is installed. The
instance asks for the portability drivers (VK_KHR_portability_enumeration, or the loader hides MoltenVK), the
device is made with VK_KHR_portability_subset, a 1.2 MoltenVK is taken with dynamic rendering and synchronization2
as extensions, and the bindless table is sized from what Metal lets a set hold. None of it has been run on an
actual Mac yet, it was all worked out by reading, so the first run there is the test.

## The Skyline debugger

A Debug build of anything made with Horizon has `Engine/Debugging/SkylineDebugger` on the engine (`engine.Debugger`),
F10 brings it up and puts it away again, and hidden there is nothing of it on screen. Up, the game is in a
container in the middle and the only place anything it draws is seen, with a menu bar over it, the scene tree on
the left (every entity and its components), the inspector on the right (the fields of whatever is selected, set
while the game runs, anything with an inside opens up a level down) and a drawer under it with the loaded
textures, render targets, shaders and buffers, what every pass costs the GPU, and the log. F8 pauses the scene, F9
steps it a tick, the Game menu slows it down (`SceneManager.Paused`, `Step`, `TimeScale`, which are there in
every build). `HORIZON_DEBUGGER=on` starts with it up.

Whatever is public and can be set is edited in the inspector without anybody doing anything. `[Inspect]` on a
private field puts that in as well, `[Inspect(0, 400)]` gives a number a slider, `[HideInInspector]` keeps
something out (do that to a property that does work when it is read, the inspector reads everything it shows
eight times a second). Both attributes are in `Horizon.Core` and are there in Release too, doing nothing.

A Release build doesn't compile the folder (the csproj takes `Engine/Debugging` out) and the three places the
engine touches it are inside `#if DEBUG`, so a game that talks to `engine.Debugger` wraps that the same way. The
game is drawn at the size of the window as always and the suite copies the finished frame off the window and
shows the copy smaller, so the numbers are the game's own plus one blit and the pass called ui. The mouse the
game reads is mapped into the container (`Mouse.Position` is where it is in the game, `WindowPosition` where it
really is) and withheld while it is over a panel, the keyboard is withheld while something is typed into a box.

## When the UI is painted

A UI hands out the pointer and the keys in its update and is laid out and painted at the end of the tick
(`UICompositor.Capture`), after every update of that tick, so a name tag a scene puts on a sprite is drawn where
the sprite is and not where it was a tick ago. `Bounds` read straight after a compositor's update are the ones of
the tick before. Frames between two ticks blend the quads of the two lists by who painted them
(`SpriteItem.Key`), list against list when they are the same quads and quad by quad when they aren't, so one
number that changes every tick doesn't have the rest drawn in steps. A component that paints rows (a menu, a
list) says so with `UIDrawList.BeginPart(row)` and `EndParts()`, or a highlight coming and going shifts the count
of everything after it and a rule gets blended with a highlight for a tick. A cursor is none of that, it would
trail the hand, `UICompositor.Cursor` (a `UICursor`) is placed on the render thread from `Mouse.LivePosition`
as every frame is drawn. The pacing example (`pacing`) measures all of it, in frames.

How often a UI is put together and drawn anew is held to `UICompositor.FrameRateLimit`, 120 times a second unless
told otherwise (`UICompositor.DefaultFrameRateLimit` for every UI made after it is set, 0 for no limit). At three
thousand frames a second a moving UI was blended, sent up and drawn three thousand times a second, now the frames
in between lay the picture of the last time over once more. It is rounded to whole frames, the UI is drawn anew on
the frame nearest to when it is due, so a screen of 144 gets it every frame and one of 240 every other, never five
out of six. Only the drawing is held back, the pointer, the updates and the painting go at the simulation's rate
and the cursor is drawn every frame regardless. Uncapped the UI trails what the scene draws by half a refresh on
average, which the pacing example shows and L there takes the limit off to compare.

## Garbage

The render thread allocates nothing in a frame, in the examples and in a fight, and it is meant to stay that way.
The trap that got it there three times over is a lambda that uses a local or a parameter of the method it is
written in. What it uses is put on the heap as the method starts, not when the lambda is reached, so a method
that runs every frame and has such a lambda on a path it never takes makes an object every frame. The lambda
goes in a method of its own that is handed what it needs. `Horizon.Tests/HotPathTests` reads the compiled engine
and fails if one of the methods it lists (the frame, the buffers, the UI, sprites and tiles) makes one, add to
the list when something new runs every frame. A buffer that changes every frame is written over with `Update` or
is a `StreamBuffer`, `Upload` on one the GPU still reads is a new Vulkan buffer every time.

## Building and testing

- `dotnet build Horizon.sln -p:EnableWindowsTargeting=true` on Linux (the HIDL editor is WinForms).
- `dotnet test Horizon.Tests`.
- `Horizon.Testing <scene>` from its output folder runs one example (`--checks` for every self check in a row), `HORIZON_INPUT_SCRIPT` (a file of lines like
  `6 quit`) to quit after a while, `HORIZON_LOG_LOOPS=1` for the numbers (the loops and the GPU passes, and how many
  bytes a turn every loop allocates), `HORIZON_LOG_ALLOCATIONS=3` for what every thread allocates by type,
  `HORIZON_SCREENSHOT=file.png@3` to see what was drawn (`file.png@3+8x0.5` for eight of them half a second apart), `HORIZON_VULKAN_VALIDATION=1` for the validation layer (when the SDK is installed), `HORIZON_SHADER_CACHE=off`
  to compile every shader anew. A Debug build tells the driver what every Vulkan object is called and labels the GPU
  scopes, so RenderDoc reads "sprites.slang pipeline" and "path tracing" rather than handles
  (`VulkanContext.Name`, `HORIZON_VULKAN_LABELS=on` for the same in Release, `off` to go without). Name a texture,
  buffer or render target (`Name`) and the debugger shows that. Headless on Linux wants a Vulkan driver, lavapipe does.
- `docs/metrics/raw/run_scenes_windows.py <bin> <label> <out.json>` runs every scene and collects the loop numbers,
  `make_vulkan_workbook.py` turns two of those into the OpenGL against Vulkan workbook. `run_passes.py` collects what
  the GPU spent on every pass (headless on lavapipe does) and `make_passes_workbook.py` makes the lighting
  performance workbook out of a before and an after of those.
- Headless on this kind of Linux box, `apt-get install mesa-vulkan-drivers vulkan-tools`, `Xvfb :99 -screen 0 1600x900x24 &`,
  `DISPLAY=:99`, and lavapipe takes it from there, slowly.

## Where everything is

So nobody has to crawl the tree again. Two libraries and four apps, one solution.

- `Horizon/` the engine, one assembly, a folder (and namespace) per area.
  `Core` (window, the three loops, snapshots and interpolation), `Logging`, `Content` (asset scopes, packs),
  `Graphics` (the Vulkan device in parts, the resource classes, the Slang compiler and preprocessor, descriptions,
  with the plumbing in `Graphics/Vulkan`, context, swapchain, memory, pipelines, descriptors, bindless table, frame
  resources), `Input`, `Engine` (GameEngine, Scene, Scene2D,
  cameras, Debugging, the suite a Debug build has on F10, see below), `Rendering` (Renderer2D and the deferred one,
  CameraBlock, Spriting with the sprite batch, atlas, Aseprite reader and sprite sheet definitions, Tiling with the
  tile map and its pathfinder, Particles, Primitives, PostProcessing, Lighting with the light tiles, the occlusion
  and sprite shadow fields and the radiance cascades, Text, Transitions), `UI` (UIX, compositor, modules,
  components, skins, drawing, the performance overlay under Diagnostics), `Physics` (world, bodies, fixtures,
  particle simulators). Run time files live at its root and copy out at the same paths, `shaders/` (Slang,
  `common/`, `lighting/`, `spritebatch/`, `tilemap/`, `particle/`, `primitives/`, `renderer2d/`, `post/`),
  `Assets/fonts/`, `Assets/uix/` (the dead_revolver and flat skins).
- `Horizon.HIDL/` the scripting language, kept apart because the WinForms editor (`Horizon.HIDL.Editor/`) wants
  it without the engine.
- `Horizon.Testing/` the example scenes, one per thing the engine does, simplest first. `Host/TestCatalog.cs` is
  the list and the order (ids like `quickstart`, `sprites`, `tilemap`, `lighting`, `pathtraced`, `town`), the levels
  are the folders `Examples/{FirstSteps,Game,Lighting,Showcase,Internals}`, each one only leaning on the ones
  before it, with `Examples/Shared/World.cs` for what the map examples share (the blob, lamps out of map objects).
  `Checks/` is the scenes that drive themselves and say whether the engine still works (`check-ui`,
  `check-tilemap` and so on, the old `ui-selftest` ids still start them), `Horizon.Testing --checks` runs the lot
  and leaves with how many failed. `Host/` is the selector and the strip over a running example.
  The art, maps and layouts of the examples are files in `Assets/examples` (a map opens in Tiled, a layout in
  Hex), made by the scripts in `Art/` where they are made at all, nothing is painted or written out as a scene
  starts and no example makes its textures in a shader. A new example brings its files the same way.
  `./Horizon.Testing <id>` runs one headless, see Building and testing above.
- `Horizon.Hex/` the layout editor, `--selftest --exit` clicks through itself. `Horizon.Tests/` xUnit.
- `docs/radiance-cascades.md` how the path traced lighting works, for whoever has to touch it or wants to build one.
- `docs/project-health-2.md` the write-up of that branch (its "Towards a second backend" is history now, the
  second backend is the only one), `docs/metrics/` the numbers, raw JSON and the scripts that made them,
  `vulkan-metrics.xlsx` being OpenGL against Vulkan on the same machine.

Fighter2D lives next door in `../Fighter2D` (repo bogzxo/fighter-2d) and references `Horizon/Horizon.csproj` and
`Horizon.HIDL/Horizon.HIDL.csproj` by relative path, so both checkouts have to sit side by side. It has notes of
its own in its CLAUDE.md.

