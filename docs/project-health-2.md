# Project Health 2: one engine library, one way of talking to the GPU

This is what the `project-health-2` branch does to Horizon and to Fighter2D, and what a game has to change to come along.

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
  `PostTechnique` is a `ScreenTechnique`; the effects no longer set sampler uniforms every frame.

## Debugging through the UI

With the UI in the same assembly as the engine, the engine's own debugging can use it:

- `Horizon.Engine.Debugging.ConsoleOverlay`: the log as it comes in and a HIDL line to the `DeveloperConsole`, over
  the game, F4. `Horizon.Testing` adds one to its host.
- `Log.Written` is raised for every log line, `DeveloperConsole.Output` for every command and answer, and
  `DeveloperConsole.Enqueue` runs a command on the simulation thread.
- `PerformanceOverlay` (now with the GPU time) stays where it was.

## Also in the branch

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

## Known state of Fighter2D

Fighter2D's `development` head (`d48e0bc`, the Aseprite characters) depends on engine work that is not on Horizon's
`development` branch (`AsepriteDocument`, `SpriteSheetDefinition.Open/Has/FrameCount`, `TextureAtlas(shared:)`,
`SpriteSource.ReadFrame`, `PhysicsWorld.OverlapsStatic`, `Image.Atlas/Mirrored/AtlasKey`). Those are the only errors
left when Fighter2D is built against this branch; the commit before it (`43c2113`) builds clean with the migration. The
migration itself is in Fighter2D's `project-health-2` branch.
