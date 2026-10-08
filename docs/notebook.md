# Notebook

Claude's running notes, kept so the next session (or the next hour) doesn't have to crawl the tree again. Goals,
what's going on, what was learned the hard way, hints to self. Read this first, then CLAUDE.md.

## How to write in here (and everywhere else in this repo)

Write like bogz does. Casual, a bit clever, the odd swear word when it earns it, exclamation marks are fine, jokes
are fine. No "Thing: explanation" colons in comments or commit messages, that reads like a bot. No "Added X" lists
in commit bodies, say what happened like a person would. Short commit titles that say what happened. Lowercase inline
comments are fine. If a sentence sounds like a press release, rewrite it.

## Goals right now (October 2026, project-health-2 after the Vulkan merge)

1. The emissive lights flicker in Fighter2D on the Japan map (test on that map only). Find why, fix, prove it with captures.
2. A fighter standing in front of a lantern blacks it out completely. Keep the sprite shadows but stop the light dying.
   Ideas from bogz, raise the light poles, give lights a height so the sprite only clips them, and add light types,
   spot and directional (cones) that rotate and animate. The Japan map should use them.
3. Post and lighting are slower on Vulkan than they were on GL (post 953 to 244 fps, lighting 820 to 201 on bogz's
   machine) while CPU bound scenes doubled. Benchmark with the GPU scopes, fix it algorithmically, Vulkan first,
   re-measure, Excel report in docs/metrics.
4. Why doesn't it run on macOS arm64. Investigate, plan, fix what can be fixed blind.
5. After all that, text rendering. Bitmap fonts out, SDF (probably MSDF) fonts in, through the bindless sprite path.
6. Fighter2D is the flagship, it should use every bell and whistle the engine has.

## What I've learned so far

- Headless on this Linux box works with lavapipe. `apt-get install mesa-vulkan-drivers vulkan-tools`, Xvfb on :99,
  then `DISPLAY=:99 HORIZON_LOG_LOOPS=2 HORIZON_INPUT_SCRIPT=quit.txt HORIZON_SCREENSHOT=x.png@5 ./Horizon.Testing lighting`.
  About 3 fps on the lighting scene, 265 ms a frame, so the GPU scope numbers are relative, not absolute. Four cores.
- The GPU scopes exist (`GraphicsDevice.BeginGpuScope`, read back in `ReadGpuTime`) but `HORIZON_LOG_LOOPS` doesn't
  print them, only the loops. Add them to the log so headless runs say what each pass costs.
- The sprite shadow field (`Lighting/SpriteShadows.cs`) is three compute passes every frame, edges, then one thread
  per column doing a serial sweep over the whole column, then one thread per row building a Felzenszwalb envelope
  serially, twice (two channels). That is a serial chain of a thousand steps on one lane per workgroup, which is
  exactly what a GPU hates. Suspect number one for the lighting scene. Jump flooding or a parallel scan would do.
- The light tiles pass is tiny (one thread a tile) and not the problem on its own, but it runs even with zero lights
  and so does the sprite field even when nothing casts.
- The cascades (`gi_cascade.slang`) stop a ray at `toWall < 0.25` world units and treat anything emissive over 0.2
  as a wall in the "glowing" channel of the sprite field, which is how a lantern lights its surroundings. Small
  emitters a few pixels wide against probes every 2 pixels of a half size picture (so 4 picture pixels apart)
  is a classic flicker setup, a ray hits the lantern one frame and misses it the next as the camera moves. The
  bounce reads last frame's result shifted by `uShift`, with Bounce 0.8, so anything that wobbles gets fed back.
- Lights in Japan come from Tiled objects with `light_*` properties (`Map/FightingStage.cs` ReadLight), the
  lantern template is `Assets/maps/objects/lantern.tx`, the map overrides to radius 100, intensity 0.75, flicker 0.2,
  shadows on. There's a "glow" tile layer with Emissive 1. Player fill lights are in `Effects/PlayerLights.cs`.
- Every light is a point in the plane, the fighter's sprite field is a hard wall between the light and whatever is
  behind the fighter. That is why a fighter in front of a lantern kills it, the lantern is in the floor plane.
- macOS, found by reading, not by running (no Mac here). `VulkanContext` creates the instance without
  `VK_KHR_portability_enumeration` and without the portability flag, so under the Vulkan SDK loader MoltenVK is
  hidden and `vkEnumeratePhysicalDevices` says zero GPUs. It also throws out any device under Vulkan 1.3 and asks
  for the 1.3 feature structs directly, MoltenVK depends on version. It never enables `VK_KHR_portability_subset`
  on the device, which the spec says you must when it's there. The bindless table wants 4096 combined samplers in one
  set, Metal limits samplers per argument buffer, size it from the limits. `shadow_columns.slang` uses 32 KB of
  groupshared, which is the whole Apple threadgroup budget. GLFW on Mac needs `libvulkan.1.dylib` or
  `libMoltenVK.dylib` findable and nothing ships it, the user has to have the SDK installed. Natives for SDL/GLFW/Slang
  all ship osx-arm64 so that's not it. Window on main thread, drawing on a render thread, that's the right way round for Cocoa.
- The window is GLFW (`WindowManager` registers GlfwWindowing) even though the csproj references the SDL packages.

## Done so far this round

- The lanterns breathing on Japan was the radiance cascade probe grid riding along with the camera. Every pixel the
  camera panned, every probe saw something slightly different and a lamp a few pixels wide got hit by a ray one
  frame and missed the next. The grid is pinned to the world now (`PathTracedLighting2D.Offset`, `uProbeOffset` in
  gi_cascade and gi_resolve, one probe more each way). Measured on the new `pathtraced-pan` example (nothing moves
  but the camera, lights steady, the blob glows), aligned frame to frame difference of the GI buffer went from 0.96
  to 0.35, and about 0.1 while the camera drifts slowly. The lights' own `Flicker` is still there on purpose.
- `HORIZON_SCREENSHOT=file.png@3+8x0.5` takes a series now, numbered, and `HORIZON_LOG_LOOPS` prints the GPU passes.
- Vsync off asks for immediate mode first, mailbox was still waiting for the blank on Windows.
- GPU passes on lavapipe, Japan, path traced, 1600x900. sprite shadows 160 to 235 ms, path tracing 200 to 265,
  tile map 50 to 77, resolve 26 to 39, the rest nothing. The sprite field is the thing to kill first.

## The performance round

- Before anything, on lavapipe at 1600x900, post 294 to 313 ms a frame of which sprite shadows 163 to 180 (and the
  post scene has no lights!), lighting 336 to 355 of which sprite shadows 177 to 194 and resolve 127 to 131,
  pathtraced 807 to 892 of which path tracing 453 to 522, sprite shadows 178 to 183, resolve 140 to 153. Japan
  path traced 532 to 700, sprite shadows 160 to 235, path tracing 200 to 266, tile map 50 to 77, resolve 26 to 39.
- The sprite shadow field was three passes, one with a thread per column doing a serial sweep and one with a thread
  per row building an envelope, both serial on one lane per workgroup. It is a jump flood now (sprite_seed,
  sprite_flood eight times, sprite_field), on a grid two texels coarse with the last pass exact, and it is skipped
  when no sprite casts or no light is in view (the tracer always wants it). Rg32Uint is a pixel format now, for the seeds.
- The cascades worked the lamps out with shadows for every ray that hit a wall. gi_radiance does that once a frame
  for the texels on or next to something solid or glowing, the rays read it.
- Light tiles are skipped with no lights, and directLight bails out with none.
- `docs/metrics/raw/run_passes.py` collects the pass numbers into JSON, `make_passes_workbook.py` makes
  `docs/metrics/lighting-performance.xlsx` out of them.
- After, on lavapipe, same scenes. post 303 to 120 ms (no field, no tiles, nothing to do), lighting 345 to 158
  (field 186 to 64, and the resolve read 129 then 63, same code, lavapipe is a CPU and the serial passes were
  starving it), pathtraced 849 to 365 (path tracing 488 to 199 with the radiance map). Japan, old map, path
  traced, 681 to 388. Japan with the new map (moon, spots, dusk) 459 path traced and 234 direct, of which the
  moon's long soft shadows are the dear part, the light cells pass is 68 with it and 19 without. Cells off on the
  new map the resolve is 120, so lighting once per pixel of the art is worth about a third of the frame there.
- Lavapipe caveat, compute passes run a lot worse than fragment passes on it than they do on a card, the jump
  flood's 63 to 74 ms is eight passes of 360 thousand threads, nothing on a GPU. The real after is a run of
  run_scenes_windows.py on bogz's machine.

## Text

- Fonts are signed distance fields now (`Rendering/Text/DistanceFieldFont.cs`). A TrueType file goes through
  stb_truetype (StbTrueTypeSharp, pure managed, MIT) straight into distances at 48 px to the em, packed on shelves
  into an R8 atlas, kerning kept. A BMFont bitmap (the pixel fonts the dead_revolver skin uses) has its picture's
  coverage turned into distances by an exact transform, halved if it is a big one. Both give metrics at 96 px to
  the em (`font: { dir, file, size }` in a skin, the size for TrueType), so text_scale means what it did. The
  sprite shader's FIELD_FLAG turns the field into an edge smoothed over one screen pixel, crisp at any size, no
  mipmaps. What was built is kept under the shader cache's parent in `Fonts/` by a hash of the file.
- Cascadia Mono is shipped as its .ttf now (OFL, README in its folder), the bitmap of it is gone. Klaxon and VCR
  stay as bitmaps and come out as fields at load.
- Ideas not done. Outlines and drop shadows are one more threshold on the same field (the spread is kept for
  that). Multi channel fields (MSDF) would keep sharp corners sharper at big sizes, that needs msdfgen which is C++.

## Emissive is not the same as a lamp

- Everything emissive used to be a lamp to the tracer, so a fighter flashed white on a hit lit the whole arena for
  the frames of the flash and every hit spark was a tiny light going on and off. That read as the lights flickering
  violently whenever anybody attacked. The material attachment's blue is "how much this lights what is round it"
  now (`gbuffer.slang`), the surface's blue stays "how much shows unlit". Tiles and primitives light as they glow,
  a sprite's flash lights nothing unless `Sprite.FlashLights`, and a particle renderer says with `Lights`. The
  sprite field's glow channel and gi_radiance read the material's blue. Fighter2D's impact sparks, blood, mist and
  the hitstun haze don't light, the lava does.

## The round after, flicker everywhere and the blur going

- The tile layers that flickered were the parallax ones. `TileMap.Draw` took the eye from `camera.Position`, which
  is where the simulation last put the camera, a tick ahead and unrounded, while the view came from `camera.Bounds`,
  the shown one. Every frame the difference moved every layer with a parallax. Now the eye is the middle of the bounds.
  Anything on the render thread that wants the camera takes it from Bounds, ViewProj or the camera block, never Position.
- The gamepad button icons "flickered" because the pressable ones have five frames at 0.12 s and cycled without
  pause. An icon with frames plays them once every `icon_blink` seconds (3) and rests on the first in between.
- The UI flicker on things that update (progress bars, a hovered button's neighbours) was almost surely the motion
  blur, which smears a moving thing over its neighbours by whole 32 pixel tiles. bogz had it removed outright,
  the motion attachment, the velocity in every shader, the UI's motion tracking, MotionEstimator, Camera.Velocity,
  the three blur passes and the game option. The G-buffer is three attachments now. If the flicker survives that,
  the next suspect is the retained UI layer (`UICompositor.DrawUploaded`, `PostLayer.Replay`).
- Questions with two buttons side by side wanted up and down on the pad because the menus only ever fed the
  navigator the vertical. Left and right go in too now, a selector still takes them first.
- Intros ease now (`intro_easing: "out_cubic"` in a layout, any Easing), a fade was the one that ran straight.
- GLFW has no Vulkan on macOS. The window is SDL's there, see CLAUDE.md.
- A sprite the light sits inside of doesn't shadow it, the shadow march walks through sprites and a sprite it is
  still inside of at the light is let off. No colliders on the lamps needed.

## Hints to self

- Shell cwd drifts between calls, use absolute paths and `git -C`.
- Build the game with `dotnet build -c Debug` in /home/user/Fighter2D, run it headless with
  `DISPLAY=:99 ./Fighter2D --map japan` from bin/Debug/net10.0, HORIZON_INPUT_SCRIPT to quit, HORIZON_SCREENSHOT@11
  (ten seconds of versus screen first).
- `options.hor` next to the exe holds the options, `fancy: true` is the path traced one.
- Commits end with the Co-Authored-By and Claude-Session lines, no model names anywhere in the code.
- Don't touch Build History in Fighter2D, it's tracked on purpose.

## Debugger names

Every Vulkan object gets a name through VK_EXT_debug_utils in a Debug build (`VulkanContext.Name`, `LabelsEnabled`),
and `BeginGpuScope` is also a `vkCmdBeginDebugUtilsLabelEXT`, so RenderDoc folds a frame up the way the performance
overlay does. `GpuResource.Name` has a `Named()` hook the texture, buffer and render target use to tell the driver,
so naming a resource after it is made works too, and a named render target names its attachments after itself.
What nobody named is told by what it is ("Storage buffer of 4096 bytes", "640 by 360 Rgba16F"). Lavapipe takes the
names without complaint, nobody has looked at them in RenderDoc yet, that wants a real GPU.
