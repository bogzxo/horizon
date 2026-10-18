# Polishing the engine

Written 2026-10-18 off a pass over the tree, the notebook, the tests, the self checks and a build of the solution, after
the game's own polish round (`Fighter2D/docs/polish-plan.md`). The engine works, draws every frame without garbage,
runs 472 tests and 458 self checks clean, and Fighter2D and Hex sit on it without complaint. What's left between it and
something somebody else could pick up is proof, a sweep of loose ends, and the first run away from this one machine.

## What's rough, by how much it costs

1. **Half the engine has no self check.** The 458 checks are the UI (seven suites) and the tile map (one). Physics,
   particles, the lights, the path tracer, the post effects, the transitions, the voxels, the meshes, the audio device
   and the scenes stacking have examples a person looks at and nothing that says they still work. A change to the
   lighting is checked by running the example and looking, which is how the camera snapping and the breathing light
   lived as long as they did. `OcclusionFieldTests` and `PathTracingTests` are the exceptions and the shape to follow.
2. **CI only runs the tests.** `.github/workflows/publish.yml` builds and runs `dotnet test`, never the checks, never
   Hex's self test, and it's on `actions/checkout@v3` and `cache@v2`, which GitHub is retiring. A headless run on
   lavapipe is written up in CLAUDE.md and has never been wired into it. The game's workflow builds against a
   submodule that's behind this checkout.
3. **The Mac has been run on once** (notebook, "The first run on an actual Mac") and Linux never outside a headless
   box. Anything a window does by hand (the frameless drag, the fullscreen toggle) is Windows and GLFW's elsewhere.
4. **Thirty warnings a build.** 14 `CS8602` nullable dereferences in the engine (ShaderCompiler, VulkanMemory among
   them), four members hiding a base one by accident (`Button.Shown()`, `Notifications.Shown`, `DrawingPanel.Navigable`,
   `BlurredBackdrop.Dispose()`), two `CS0649` test fields never assigned, the HIDL trim notes. None of them bites yet,
   `BlurredBackdrop.Dispose()` hiding `Entity.Dispose()` is the kind that will.
5. **Two TODOs older than the voice.** `PhysicsBodyComponent2D.Position` is publicly settable from anywhere while the
   world is simulating it (its own comment says so), and `SpritesheetAnimationManager` carries a note about a method
   that was gutted. Everything else that says it's unfinished is in the notebook, not the code.
6. **Twenty one `~syncthing~*.tmp` files** in the tree, ignored by git but sitting in folders no example lives in any
   more (`Examples/Engine`, `Input`, `Physics`, `Rendering`, `UI`), the ghosts of the sorting. The folders with
   nothing but them in should go.
7. **Four files past a thousand lines.** `CodeEditor.cs` (2164, with three partials already), `UITextDocument.cs`
   (1522), `TileMap.cs` (1473), `WindowManager.cs` (1189), `UIRichText.cs` (1125), `UICompositor.cs` (1034). Each is
   one thing and reads top to bottom, so no rush, but the next person opens `TileMap.cs` to change the colliders and
   scrolls past the GPU upload and the pathfinder to get there.
8. **The docs lag the code in places.** CLAUDE.md says 442 checks (458), Hex's 413 (424), the notebook is 1700 lines of
   diary nobody reads from the top, `project-health-2.md` is a write-up of a branch that is history, `docs/metrics` is
   OpenGL against Vulkan when there is no OpenGL. There is no page that says how to make a game with it, the examples
   are that page.
9. **No release.** Nothing is tagged, nothing is a NuGet package, a game takes the engine as a submodule and builds all
   of it. Fine for the three repos here, nothing a fourth could use.

## Where it should end up

An engine that proves itself. Every area has a check that fails when it breaks, the checks run on every push on a box
that isn't this one, the warnings are zero and stay zero, the tree has nothing in it that isn't used, and a page says
how to start.

## The order to do it in

### 1. Self checks for the rest (the big one)

A check scene per area the way the UI has them, driven by updates with `Settle` and `WaitFor`, read back off the GPU
where a picture is the proof (`HORIZON_SCREENSHOT` already copies the frame, a check reads the same pixels), numbers
where numbers are.

- `check-physics`, a body falls at its gravity and no faster than its top speed, a fixture reports a hit, the character
  controller steps a slab and stops at a wall, the fluid keeps its particles.
- `check-particles`, a burst is as many as asked, they age out, the GPU count matches, the physics simulator lands on
  the ground.
- `check-lighting`, a point light lights a pixel and a wall shadows one, a spot's cone, the sprite shadow, the
  ambient occlusion darkens a corner, the geometry occlusion, by reading the lit picture at known pixels.
- `check-pathtraced`, the cascades converge (the picture stops changing), a light off the screen bounces in, the fog,
  three cascades within so much of all of them.
- `check-post`, bloom lifts a bright pixel's neighbours, exposure adapts over its seconds, each transition covers and
  uncovers in its time with no frame drawn twice.
- `check-scenes`, Set, Open, Return, ResetTo, Preload and a transition between, what Initialize and Dispose get called
  and when (the stacking of the notebook's "Scenes stack now").
- `check-audio`, the mixer under the real device, a sound queued is drained, nothing plays with `HORIZON_AUDIO=off`.
- `check-voxels` and `check-meshes`, a tree built and traced hits what was set, a mesh draws its triangles.

Each one is a day or less and the UI ones are the template. `Horizon.Testing --checks` grows to run them all.

### 2. CI that runs it all

The workflow on current actions, Windows and Ubuntu, the Ubuntu job on lavapipe under Xvfb the way CLAUDE.md says,
running the tests, `Horizon.Testing --checks` and (checked out next to it) Hex's `--selftest`. A badge on the README.
A nightly that publishes the examples as a zip, so the Mac run has something to download.

### 3. Zero warnings

The 14 nullable ones fixed or annotated where the code knows better, the four hidden members made `override` or `new`
on purpose (`BlurredBackdrop.Dispose()` should override), the test fields assigned or `[Inspect]`ed, the HIDL trim
notes suppressed where the reflection is deliberate. Then `TreatWarningsAsErrors` on the engine so they stay at zero.

### 4. The loose ends

`PhysicsBodyComponent2D.Position` becomes `Teleport` for the game and the setter internal to the world, the
`SpritesheetAnimationManager` note resolved, the syncthing ghosts and their empty folders gone (and a `.gitignore`
line for `~syncthing~*` if it's not there), `TileMap.cs` in parts (the file, the layers, the GPU, the colliders and
shadows, the pathfinder), `WindowManager.cs` likewise (it has three partials, the main is still 1189).

### 5. The docs

CLAUDE.md's counts right, `project-health-2.md` and the OpenGL metrics into `docs/history/` with a line each saying
what they were, the notebook kept but a `docs/README.md` that is the ten minute way in (make an engine, a scene, a
sprite, a map, a light, a UI, a sound, run the checks) with links into the examples, and a page a subsystem for the
ones that have none (the UI, the physics, the audio, the way the loops and snapshots work).

### 6. The second run on a Mac and a Linux desktop

Everything above makes this cheap, the checks say what broke. The frameless window and fullscreen by hand on both,
the gamepads, the audio device, MoltenVK's limits. Whatever the first run wrote up (the notebook) checked off.

### 7. A release

A tag, a changelog out of the notebook's headings, the engine as a NuGet package (`Horizon` and `Horizon.HIDL`) with
its shaders and assets as content files, so a game is a `PackageReference` and not a submodule, and the three repos
here switched to it when it works.

## Decided with bogz (2026-10-18)

- **The checks first**, the most proof for the time, then the CI they land into.
- **No NuGet.** The submodule stays the way a game takes the engine, step 7 is a tag and a changelog at most.
- **The Mac later.** Step 6 stays, run once the rest is in.
