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

Where that paid off is written into the code. Sprites and the UI are vertexless quads out of a storage block, tile
maps live on the GPU as chunks a compute pass culls into one indirect draw, particles are simulated and compacted on
the GPU and drawn indirect, lights are sorted into screen tiles, the occlusion map is a signed distance field the
shadows march, sprites that cast shadows do so through a distance field of the picture built every frame, and the
path traced lighting is radiance cascades in compute. The performance overlay (`UI/Diagnostics/PerformanceOverlay`,
F3) shows what every pass of a frame costs the GPU, the threads, the unevenness of the frames and the garbage.

## Building and testing

- `dotnet build Horizon.sln -p:EnableWindowsTargeting=true` on Linux (the HIDL editor is WinForms).
- `dotnet test Horizon.Tests`.
- `Horizon.Testing <scene>` from its output folder runs one example, `HORIZON_INPUT_SCRIPT` (a file of lines like
  `6 quit`) to quit after a while, `HORIZON_LOG_LOOPS=1` for the numbers, `HORIZON_SCREENSHOT=file.png@3` to see what
  was drawn, `HORIZON_VULKAN_VALIDATION=1` for the validation layer (when the SDK is installed), `HORIZON_SHADER_CACHE=off`
  to compile every shader anew. Headless on Linux wants a Vulkan driver, lavapipe does.
- `docs/metrics/raw/run_scenes_windows.py <bin> <label> <out.json>` runs every scene and collects the loop numbers,
  `make_vulkan_workbook.py` turns two of those into the OpenGL against Vulkan workbook.

## Where everything is

So nobody has to crawl the tree again. Two libraries and four apps, one solution.

- `Horizon/` the engine, one assembly, a folder (and namespace) per area.
  `Core` (window, the three loops, snapshots and interpolation), `Logging`, `Content` (asset scopes, packs),
  `Graphics` (the Vulkan device in parts, the resource classes, the Slang compiler and preprocessor, descriptions,
  with the plumbing in `Graphics/Vulkan`, context, swapchain, memory, pipelines, descriptors, bindless table, frame
  resources), `Input`, `Engine` (GameEngine, Scene, Scene2D,
  cameras, Debugging, which is waiting on a new debugger and dashboard, the old ones went), `Rendering` (Renderer2D and the deferred one,
  CameraBlock, Spriting with the sprite batch, atlas, Aseprite reader and sprite sheet definitions, Tiling with the
  tile map and its pathfinder, Particles, Primitives, PostProcessing, Lighting with the light tiles, the occlusion
  and sprite shadow fields and the radiance cascades, Text, Transitions), `UI` (UIX, compositor, modules,
  components, skins, drawing, the performance overlay under Diagnostics), `Physics` (world, bodies, fixtures,
  particle simulators). Run time files live at its root and copy out at the same paths, `shaders/` (Slang,
  `common/`, `lighting/`, `spritebatch/`, `tilemap/`, `particle/`, `primitives/`, `renderer2d/`, `post/`),
  `Assets/fonts/`, `Assets/uix/` (the dead_revolver and flat skins).
- `Horizon.HIDL/` the scripting language, kept apart because the WinForms editor (`Horizon.HIDL.Editor/`) wants
  it without the engine.
- `Horizon.Testing/` the example scenes, one per feature, `Examples/{Basics,Engine,Input,Physics,Rendering,UI}` with
  `Host/TestCatalog.cs` listing them (ids like `quickstart`, `lighting`, `pathtraced`, `fluid`, `ui-selftest`).
  `./Horizon.Testing <id>` runs one headless, see Building and testing above.
- `Horizon.Hex/` the layout editor, `--selftest --exit` clicks through itself. `Horizon.Tests/` xUnit.
- `docs/project-health-2.md` the write-up of that branch (its "Towards a second backend" is history now, the
  second backend is the only one), `docs/metrics/` the numbers, raw JSON and the scripts that made them,
  `vulkan-metrics.xlsx` being OpenGL against Vulkan on the same machine.

Fighter2D lives next door in `../Fighter2D` (repo bogzxo/fighter-2d) and references `Horizon/Horizon.csproj` and
`Horizon.HIDL/Horizon.HIDL.csproj` by relative path, so both checkouts have to sit side by side. It has notes of
its own in its CLAUDE.md.

