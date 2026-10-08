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

## Where the rendering is going

The rendering logic is to be kept apart from the OpenGL backend, so a different backend (Vulkan is the one in mind)
can be dropped in later. Everything outside `Horizon/OpenGL` talks to the GPU through `Horizon.Graphics`
(`GraphicsDevice` and the resource classes), never through Silk's `GL` directly. New rendering code goes through the
device; anything that needs a GL call the device hasn't got gets the device extended, not a `GL.` sprinkled into a
renderer. What is still GL flavoured and has to go before a second backend exists is listed in
`docs/project-health-2.md` under "Towards a second backend".

## Building and testing

- `dotnet build Horizon.sln -p:EnableWindowsTargeting=true` on Linux (the HIDL editor is WinForms).
- `dotnet test Horizon.Tests`.
- Headless on Linux, with Xvfb and Mesa, `MESA_GL_VERSION_OVERRIDE=4.6 MESA_GLSL_VERSION_OVERRIDE=460 ./Horizon.Testing <scene>`,
  `HORIZON_INPUT_SCRIPT` to quit after a while, `HORIZON_LOG_LOOPS=1` for the numbers, `HORIZON_SCREENSHOT=file.png@3`
  to see what was drawn.

## Where everything is

So nobody has to crawl the tree again. Two libraries and four apps, one solution.

- `Horizon/` the engine, one assembly, a folder (and namespace) per area.
  `Core` (window, the three loops, snapshots and interpolation), `Logging`, `Content` (asset scopes, packs),
  `Graphics` (the GraphicsDevice the renderers talk to), `OpenGL` (the only backend, and the only place `GL.` lives,
  with the resource classes, factories, StreamBuffer, GpuTimer), `Input`, `Engine` (GameEngine, Scene, Scene2D,
  cameras, Debugging with the console overlay, WebHost dashboard), `Rendering` (Renderer2D and the deferred one,
  CameraBlock, Spriting with the sprite batch, atlas, Aseprite reader and sprite sheet definitions, Tiling with the
  tile map and its pathfinder, Particles, Primitives, PostProcessing, Lighting with the path tracer, Text,
  Transitions), `UI` (UIX, compositor, modules, components, skins, drawing), `Physics` (world, bodies, fixtures,
  particle simulators), `Webhost`. Run time files live at its root and copy out at the same paths, `shaders/`
  (`common/`, `lighting/`, `spritebatch/`, `tilemap/`, `particle/`, `primitives/`, `renderer2d/`, `post/`),
  `fonts/`, `Assets/uix/` (the dead_revolver and flat skins), `web_host/`.
- `Horizon.HIDL/` the scripting language, kept apart because the WinForms editor (`Horizon.HIDL.Editor/`) wants
  it without the engine.
- `Horizon.Testing/` the example scenes, one per feature, `Examples/{Basics,Engine,Input,Physics,Rendering,UI}` with
  `Host/TestCatalog.cs` listing them (ids like `quickstart`, `lighting`, `pathtraced`, `fluid`, `ui-selftest`).
  `./Horizon.Testing <id>` runs one headless, see Building and testing above.
- `Horizon.Hex/` the layout editor, `--selftest --exit` clicks through itself. `Horizon.Tests/` xUnit.
- `docs/project-health-2.md` the write-up of the branch, `docs/metrics/` the numbers, raw JSON and the scripts
  that made them.

Fighter2D lives next door in `../Fighter2D` (repo bogzxo/fighter-2d) and references `Horizon/Horizon.csproj` and
`Horizon.HIDL/Horizon.HIDL.csproj` by relative path, so both checkouts have to sit side by side. It has notes of
its own in its CLAUDE.md.

