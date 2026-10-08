# Project Health 2, one engine library and one way of talking to the GPU

This is what the `project-health-2` branch does to Horizon and to Fighter2D, and what a game has to change to come along.
The numbers are in `docs/metrics/project-health-2-metrics.xlsx`.

## The layout

Horizon used to be eleven projects that referenced each other in a line (logging, core, content, OpenGL, input, web host,
engine, rendering, UI, physics, and the scripting language). The line meant the UI could not be used by the engine's own
debugging without a circular reference, the physics had to carry a renderer of its own for its debug outlines, and the
rendering code was split between an `OpenGL` project that abstracted the GPU and a `Rendering` project that half went
around the abstraction.

Now there are two libraries:

| Project | What it is |
| --- | --- |
| `Horizon` | The engine. One assembly, with a folder (and namespace) per area: `Logging`, `Core`, `Content`, `OpenGL`, `Input`, `Webhost`, `Engine`, `Rendering`, `UI`, `Physics`. The shaders, fonts, UI skins and dashboard pages it needs at run time live at its root and copy to the output at the same paths as before (`shaders/...`, `fonts/...`, `Assets/uix/...`, `web_host/...`). |
| `Horizon.HIDL` | The scripting language. It stays a project of its own because `Horizon.HIDL.Editor` (WinForms) wants the language and nothing of the engine. |

The applications (`Horizon.Testing`, `Horizon.Hex`, `Horizon.Tests`, `Fighter2D`) reference those two.

### Namespaces that moved

| Before | After |
| --- | --- |
| `Bogz.Logging` | `Horizon.Logging` |
| `Horizon.Rendering.UIX` (and `.Components`, `.Drawing`, `.Skinning`, `.Scripting`) | `Horizon.UI` (and `.Components`, `.Drawing`, `.Skinning`, `.Scripting`) |
| `Horizon.Engine.Webhost` | `Horizon.Engine.WebHost` |

Everything else kept its namespace. A game migrates with two search-and-replaces.

### What was removed

Dead or superseded code went: the 3D leftovers (`Mesh`, `Mesh3D`, `MeshGenerator`, `Material`, `MaterialFactory`,
`Vertex3D`, `Camera3D`, `TransformComponent3D`, the `basic` and `font` shaders), the unused GLSL snippets, the
`Horizon.Physics.Debug` renderer and its shaders, `RenderRectangle`, the geometry shader of the old primitive renderer,
`Newtonsoft.Json` (the dashboard serializes with `System.Text.Json` and a source generated context, which trims and
compiles ahead of time), and a stray `Fighter2D/Player` folder that was checked into the engine repository.

## The GPU layer

There are two halves to it now. `Horizon.Graphics.GraphicsDevice` is the GPU as far as the renderers are concerned,
and `Horizon.OpenGL` is the one backend there is. Nothing outside `Horizon/OpenGL` calls GL. The renderers draw, clear,
set the stencil, bind their textures, read pixels and wait on fences through `GraphicsDevice.Current`, and get their
buffers, textures, vertex arrays and techniques from the resource classes, which do the GL. The window makes the device
along with its context, and the engine's log gets the backend's debug output through it.

### Towards a second backend

The device is the seam a Vulkan (or whatever) backend goes behind, and it is not all the way there yet. What is still
OpenGL shaped and would have to move before a second backend is real, in the order it would bite:

- The resource classes (`BufferObject`, `Texture`, `FrameBufferObject`, `VertexArrayObject`, `Shader`/`Technique`) are
  concrete GL classes that the renderers hold directly, and their descriptions use Silk's enums (`BufferTargetARB`,
  `InternalFormat`, `FramebufferAttachment`, `TextureUnit`). They would become interfaces the device hands out, with
  enums of our own.
- `RenderState` (blending and the depth test) sets GL itself. It would go through the device like the stencil does.
- Techniques set uniforms by name. A second backend wants everything in blocks (`CameraBlock` is the start of that),
  with push constants for the handful a draw changes.
- The shaders are GLSL 460 with `layout(binding = N)` everywhere, which is most of the way to SPIR-V already. The
  `#include` preprocessor would stay, it is ours.
- `ObjectManager` is where the GL context lives and the factories are GL. The device would own the factories.

The rule in the meantime (it is in CLAUDE.md too): a renderer that needs something the device hasn't got adds it to
the device, it does not reach for `GL`.

Everything goes through direct state access (OpenGL 4.5) and nothing binds to be filled or described any more.

- **`BufferObject`** has `Upload`, `Allocate`, `Update`, `Storage`, `Map`/`Unmap`, `BindBase`/`BindRange`. The
  bind-to-upload paths (`BufferData`, `BufferSubData`, `VertexAttributePointer` on a buffer) are gone.
- **`VertexArrayObject`** describes its attributes itself: `SetLayout<T>(binding, buffer, divisor)` for a struct that
  implements `IVertex`, or `SetAttribute`/`SetVertexBuffer`/`SetElementBuffer` by hand. The element buffer is part of
  the array, so a draw binds the array and nothing else. `VertexBufferObject.SetLayout<T>()` and
  `SetInstanceLayout<T>()` do the common cases.
- **`StreamBuffer<T>`** is the one persistent-mapped, triple-buffered, fenced ring for everything written every frame:
  sprite items, shapes, the instances of the CPU particle simulators (which each had a copy of that code before). The
  region of a frame is bound as a range, so shaders index it from zero.
- **`CameraBlock`** is a std140 uniform block (binding 0, `shaders/common/camera.glsl`) with the view, projection, their
  product and inverse, the camera's velocity, the motion scale, the viewport and the clocks. The engine binds it once a
  frame; a renderer calls `CameraBlock.Use(camera)` before it draws, which only writes when the camera or the frame
  changed. No shader asks for `uCameraView`/`uCameraProjection` any more.
- **Shaders** are all `#version 460 core`, include what they share (`#include <common/camera.glsl>`,
  `<common/motion.glsl>`, `<common/gbuffer.glsl>`; the preprocessor resolves `<...>` from the shaders root, `"..."`
  beside the file, includes nest and come in once), and say where their samplers and blocks are bound
  (`layout(binding = N)`), so no sampler uniforms are set per draw. Textures go to their units in one `glBindTextures`.
- **`ScreenTriangle`/`FullScreenPass`**: the one triangle every whole-picture pass draws has no vertices at all
  (`shaders/common/screen.vert` makes them from `gl_VertexID`). `FullScreenPass` replaces `RenderRectangle` for a
  technique that covers the whole of a renderer; `ScreenTechnique` makes a technique from a fragment shader alone.
- **`UnitQuad`** is the one quad every instanced quad renderer draws (sprites, shapes), instead of one per mesh.
- **`GpuTimer`** wraps timer queries; `GameEngine.GpuFrameMs` is how long the GPU took over the last frame, and the
  `PerformanceOverlay` shows it next to the CPU time.

### Renderers

- **Sprites**: `SpriteBatchMesh` writes into a `StreamBuffer<SpriteItem>`, draws with `glDrawElementsInstancedBaseInstance`
  (a run that starts partway through the frame reads `gl_BaseInstance`), binds its four textures in one call and sets
  three uniforms per run instead of fourteen.
- **Tiles**: `TileMapBatch` lays `TileInstance` over an instance buffer with the DSA layout, binds its three images in
  one call, and reads the camera from the block.
- **Particles**: the quad and the instance attributes are described through the array; the CPU simulators (`CpuParticleSimulator2D`,
  `PhysicsFluidParticleSimulator2D`) share `StreamBuffer<T>` instead of their own rings and fences.
- **Primitives**: `PrimitiveRenderer` is new. Shapes are signed distance fields drawn as instances of the unit quad:
  boxes (rounded if asked), discs, segments with round caps and triangles, filled or as outlines of any thickness,
  smooth at the edge whatever the size. The simulation writes them into `renderer.Shapes` (a `ShapeList`, with a helper
  for every shape) or in `renderer.Describe`, they are published at the end of every tick and frames show them on their
  way between two ticks. One draw call, however many shapes. It writes all four G-buffer outputs so it works inside a
  `DeferredRenderer2D`.
- **Physics debug**: `PhysicsWorld.DebugRenderer` is a `PrimitiveRenderer`; set `RenderDebug` and the outlines come
  out through it (and `DebugLineWidth` says how thick). The separate line renderer, its shader and its VAO are gone.
- **Renderer2D / post processing**: the picture goes to screen through `Renderer2D.Technique` and the screen triangle;
  `PostTechnique` is a `ScreenTechnique`; the effects no longer set sampler uniforms every frame. A renderer with
  `FollowWindow` set is remade at the window's size whenever that changes, so a game doesn't have to watch for it.

### Lighting

`DeferredRenderer2D` has a `Lighting` mode. `Direct` is what it did before, lights with normal maps, specular and the
shadows of an `OcclusionMap2D`, now in `shaders/lighting/direct.glsl` where both modes share it. `PathTraced` is the
fancy one. Every frame it draws the scene as the tracer sees it at half size (what gives off light and what stops it),
floods that into a distance field (jump flooding, two seed sets so a wall knows where the nearest open texel is), and
then sphere traces a couple of dozen rays out of every texel, bouncing what it hits back through the previous frame's
result so light goes round more than one corner. The result is accumulated over frames (reprojected when the camera
moves), blurred edge-aware, and composed with the direct lighting, so the normal maps and the specular still show
through. `PathTracedLighting2D` holds the knobs (rays, steps, reach, bounce, smoothing, how big and bright a light is
as a thing to hit, strength) and `ShowTracedLight` shows the tracer's buffer on its own. On llvmpipe it is slow, on a
GPU it is a few milliseconds at half size, and it is off unless asked for.

### Scene2D

`Scene2D` is a scene with the usual lot in it, for a game that wants to draw something rather than wire a renderer up
first. A camera the size of the window, a renderer that follows the window (lit if asked, with the fancy lighting if
asked), a sprite batch and a primitive renderer inside it, and a UI over the top. `Fancy` flips the lighting mode.
`Horizon.Testing`'s `quickstart` scene is the whole of it in forty lines.

## Debugging through the UI

With the UI in the same assembly as the engine, the engine's own debugging can use it:

- `Horizon.Engine.Debugging.ConsoleOverlay`: the log as it comes in and a HIDL line to the `DeveloperConsole`, over
  the game, F4. `Horizon.Testing` adds one to its host.
- `Log.Written` is raised for every log line, `DeveloperConsole.Output` for every command and answer, and
  `DeveloperConsole.Enqueue` runs a command on the simulation thread.
- `PerformanceOverlay` (now with the GPU time) stays where it was.

## What the numbers said, and what got fixed because of them

The first sweep of the branch came out a third slower than development on every scene, under llvmpipe. The GPU timer,
the fences and the camera block were all cleared one by one (the camera block got a ring out of it anyway, see
`CameraBlock`). It turned out to be the new console overlay, which hides itself by switching its module off, and a
compositor with nothing on it still replayed its retained layer over the whole window every frame, a full screen
blend of nothing. The performance overlay was doing the same whenever it was off, on development too. A compositor
with no items now draws nothing, and the branch came out ahead of development on the scenes that have a UI over them
(which is all of them, the test host puts one there). Moral of the story, measure before believing, and a full screen
pass is never free on a software rasterizer. `HORIZON_GPU_TIMER=off` switches the timer queries off for the drivers
that flush on them.

## Also in the branch

- Screenshots. F12 saves one to `screenshots/`, and `HORIZON_SCREENSHOT=path@seconds` takes one on its own at that
  time, which is how every scene in this branch was checked headless (Xvfb, llvmpipe with the GL 4.6 override).
- `shaders/particle/simulate_physics.comp` is beside the other particle shaders again, where
  `PhysicsParticleSimulator2D` looks for it. The merge had left it in a `physics` folder, and the rain of the fluid
  scene (and the embers of a fight) went with it.
- `TileMap.CreatePathfinder()` and `TileMapPathfinder`: the way across a map over its solid tiles for whoever walks it
  without a thumb steering them (walks, jumps up and across, drops off ledges), which Fighter2D's `StageRoute` uses.
- The one compile error on `development` (`[with(128)]` in `DeveloperConsole`) is fixed, the nullable warnings of the
  engine are gone, and `Horizon.HIDL.Editor` restores on any OS (`EnableWindowsTargeting`).
- The CI workflow uses the .NET 10 SDK.

## Migrating a game

1. Reference `Horizon\Horizon.csproj` and `Horizon.HIDL\Horizon.HIDL.csproj` instead of the eleven projects.
2. Replace `Bogz.Logging` with `Horizon.Logging` and `Horizon.Rendering.UIX` with `Horizon.UI` everywhere.
3. `new RenderRectangle(technique)` becomes `new FullScreenPass(technique)`; a technique that drew a full screen quad with
   its own vertex shader uses `shaders/common/screen.vert` (or makes its corners from `gl_VertexID`).
4. A shader of your own that asked for `uCameraView`/`uCameraProjection` includes `<common/camera.glsl>` and uses
   `uViewProjection` (and the technique calls `CameraBlock.Use(camera)` instead of setting them).
5. `PrimitiveRenderer`: `Shapes.Add(new ShapePrimitive(...))` becomes the `ShapeList` helpers (`shapes.Box`, `FillCircle`,
   `Line`, `Rectangle`...); rotations are radians, sizes are full sizes, colours take an alpha. `UploadAll`/`ViewMatrix`
   are gone, the renderer reads the camera itself.
6. Buffers: `NamedBufferData` is `Upload`, `NamedBufferSubData` is `Update`, `MapBufferRange` is `Map`; a vertex layout
   is `vertexBuffer.SetLayout<T>()` with nothing bound.

## Fighter2D

The migration is in Fighter2D's `project-health-2` branch, with a `Fancy lighting` switch on the look tab of the options
(`GameOptions.Fancy`, off by default) that puts both the fight and the map preview on the path traced lighting. It
takes straight away from the options screen like the CRT switch does.

### Known state of Fighter2D

Fighter2D's `development` head (`d48e0bc`, the Aseprite characters) depends on engine work that is not on Horizon's
`development` branch (`AsepriteDocument`, `SpriteSheetDefinition.Open/Has/FrameCount`, `TextureAtlas(shared:)`,
`SpriteSource.ReadFrame`, `PhysicsWorld.OverlapsStatic`, `Image.Atlas/Mirrored/AtlasKey`). Those are the only errors
left when Fighter2D is built against this branch; the commit before it (`43c2113`) builds clean with the migration. The
migration itself is in Fighter2D's `project-health-2` branch.
