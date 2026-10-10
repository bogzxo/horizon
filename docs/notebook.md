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

## The tile map draws in layer order now, it didn't

The cull compute handed the draws their slots with an atomic, so the chunks were drawn in whatever order the GPU's
threads got there, a different one every frame. With blending and no depth that meant the lantern of one layer drew
over the glowing lantern of the layer above it or not, by luck, and on lavapipe the luck went one way and on a real
GPU another (hiding a layer changed the chunk count and the luck with it). Every chunk now has a draw at its own
index, an empty one when it is out of view, and the count is the chunk count. If the layers of a map ever look
shuffled again, that is the first place to look. `CastsShadows` is also only ever what a layer says now, a
collidable layer casts nothing unless it asks, the colliders of the game maps are hidden and not what the light
should see. And a point inside a wall takes no sprite shadows, so a fighter's shadow stops at the ground instead of
running on down through it.

## UI quads have names now

Two captured UI draw lists are blended for the frames between ticks, and they were blended by place in the list,
quad i with quad i. Plain rectangles all look alike to that (no texture, same flags), so a hover that put one quad
in or took one out had every quad after it blended with its neighbour's, a button background halfway to the next
button's, which is the bar across the row in bogz's screenshot and the "nearby buttons flicker" from before. Every
quad now carries a key in `SpriteItem.Key` (the component that painted it and which of its quads it is, see
`UIDrawList.PushOwner`) and two lists only blend when every key lines up, otherwise the newer one is shown as it is.
Text out of the distance field is read bilinear now, the pixel art filter (`smoothTexel`) was being applied to the
field too and at a big size it made a staircase of every edge. `BlocksLight` on an object layer puts the objects
into `ShadowCasters()`.

## Ambient occlusion and where the floors stop the shadows

The baked occlusion of the tiles (`_ao` next to the image, white for open) rides in the green of the material
attachment, scaled under 0.4 (`packOcclusion` in gbuffer.slang), where the sprites that cast shadows write their
coverage above 0.5, so the two never meet and `writeFlat` says wide open. Every texture unit of the deferred
shader is spoken for, a fourth attachment would have had to come in through the bindless table.

A thing that cost an afternoon. A struct in a storage block has its stride rounded up to 16 bytes by the layout
the shaders get, and the C# side (Sequential, Marshal.SizeOf) doesn't. Every struct we had happened to be a whole
number of 16 (SpriteItem 64, Tile 64, Chunk 32, Layer 48, LightData 80), so nobody noticed, and the moment Tile
grew to 72 the GPU read every tile shifted by 8 bytes a tile, garbage bindless slots, segfault in lavapipe's
compiled shader, no message. Keep every struct that goes in a block a whole number of 16 bytes, pad it by hand,
and say so in the comment next to it. The screen space part (`ambientOcclusion` in deferred.slang) marches a handful of short rays
through the distance field of walls and sprites, a point inside a wall starts at its face and only looks the way
the face does, which is why a flat floor stays open and its corners don't. `AmbientOcclusion2D` holds the knobs,
`Show` is lighting mode 3. It lives in the deferred resolve and not in the cascades on purpose, the cascades see the
world at probe spacing and this is contact scale, and it costs a few dozen field reads a pixel.

`shadow()` returns 1 straight away for a point inside a wall unless `ShadowsInsideWalls` is on, so the ground body
is lit as its face without the bands of the crates and post bases running down through it. That is what was asked
for with "the floors stop shadows", the maps already have CastsShadows on their floors.

## The ambient occlusion, a day later

Looked at properly (the Japan fight, CRT off, `Show` on) it had two things wrong with it. Every fighter was
speckled black, a pixel of a sprite that casts shadows is inside the sprite field, so every ray it sent hit its
own sprite at the first step. And the body of the ground had bars standing in it under every foot and a hatched
mess under every crate, a point in a wall was moved out to its face by the slope of the whole field, sprites and
all, and next to a ridge of that field the slope points anywhere. Now only the walls say where the face is
(`wallsDistance`), a point that is still inside after the move gets moved once more and left open if that didn't
do it either, what the face found is faded out by how deep the point is, and a pixel whose material green says
"I am a sprite that blocks light" marches the walls only.

`Strength` is clamped to 1, so 10.6 is 1, which is why cranking it did nothing past a point. `DirectStrength` is
the one that makes it show under a lamp, 0.4 now to begin with, with none of it the corners right under the
lanterns were as flat as before. The defaults are 16, 0.85, 8 samples and that.

The `_ao` maps Japan came with were blurred outlines of every silhouette, 0.79 at the darkest, which darkened
the rim of things and said nothing about a window. `Art/tools/build_ao.py` in Fighter2D paints them out of the
albedo now, a texel darker than the opaque texels round it is in a hole, over four sizes of neighbourhood, a
lone dark texel is dithering and left alone, nothing is blurred. `--depth` is how dark the deepest hole goes
(0.5 as shipped, bogz wanted less out of the paint and more out of the geometry).

`TileMap.GeometryOcclusion` is that geometry. `TileMapGeometryOcclusion` fills a grid a cell an art pixel with
`ShadowCasters()`, smears it with a box twice (a tent, running sums), and what the smear left on an open cell is
how hemmed in it is, R8, bindless, its slot and the map's corner and size in what used to be the padding of
`TileMapGpu.Layer` (tilemap_cull.slang has the same struct and wants the same edit, 64 bytes since
`TileMap.OcclusionMapStrength` went in, which is how much the painted maps count apart from the geometry). A layer that
is the geometry gets NO_TEXTURE. It is made again when a `CastsShadows` layer's version, the reach or the
strength changes, on the render thread, off the live tiles, which is fine for a map that changes once in a while
and not what you want for one that is dug through every tick.

What it costs on the 3060 at 1920 by 1080, the Japan fight, the resolve pass going from about 0.09 ms to
somewhere between 0.2 and 0.45 with eight samples marched (the readings wander that much from run to run, the
fight is never the same twice), about 0.2 with four. The maps and the geometry are inside the noise. The march
runs for every screen pixel although the lighting is worked out a `LightingPixelSize` cell at a time, moving it
into light_cells.slang would cut it by the square of the zoom and nobody has done that yet.

## A pot is not a square

The pots of Japan sat in a pale square once the corners went dark. `ShadowCasters()` says a tile blocks light or
it doesn't, so the whole 16 by 16 cell of a pot was wall, and the wall behind the see-through corners of its tile
was "inside something solid", lit as a face, no shadow on it, no occlusion either. It had been like that since the
floors stopped the shadows, the occlusion only made it show. `TileMap.ShadowCasterTexels(perTile)` gives what
blocks light by the shape of the art now (`TileMapSilhouettes` keeps the alpha of every tile set image on the CPU,
read off the file the first time, turned over the way the tile is), `TileMap.CreateOcclusion()` makes an
`OcclusionMap2D` out of that with a cell a texel of the art and a texel of the field a cell
(`fieldTexelsPerCell`, 8 for a grid of tiles as before, 1 here), and the geometry occlusion is made from the same.
For Japan the field went from 768 to 1536 on a side with a texel a world unit instead of two, and the passes cost
what they did. Objects on a `BlocksLight` layer are still the box round them, there is no picture to ask.

## Trimmed frames in the UI hung upside down

`UIDrawList.Image(TextureAtlas, ...)` put the kept part of a trimmed frame back by its offset counted from the
bottom of the rectangle, and the offset counts rows from the top while UI space has Y going up. Every character
portrait (versus screen, character select, the cells) floated at the top of its box with the empty air under the
feet. It hangs from `rect.Max.Y` now. `Sprite` always had it right.

## The examples, put in order

Horizon.Testing had grown sideways. Thirty one rows in the selector, ten of them UI and four of those the same
scene again with a pointer clicking through it, five of them the one lighting scene started with different
flags, a tile map example that wrote its maps out of string literals into the temp folder as it started, and a
brick wall that was a shader making itself up because nothing in the assets had a normal map. It is twenty one
examples now, numbered, simplest first, in five levels that are also the folders, and five checks kept apart
from them.

What was kept as it was, only moved, quickstart, entities, shapes (was primitives), keyboard and mouse, gamepads,
cameras, tweens, transitions, particles, fluid, pacing and the skin one. They were fine. What they painted in code
is a file now (`Assets/examples/camera`, `input`, `sprites`, `transitions`), the pictures the old painters made,
frozen, with the small white ones made by `Art/make_small_art.py`. The camera example still has its `TerrainAt`,
that is where the land is and the hero asks it, the island picture was painted from it once. The hive glow in the
entities example is still a texture made in `Initialize`, on purpose, it is there to show who owns what on the GPU.

What is new. `Art/make_world.py` paints one 16 pixel tile set with its normal, specular and occlusion maps (out
of a height a texel, so they can't disagree with the picture) and writes two maps, `town.tmx` and `cellar.tmx`,
with their lamps as objects out of templates. The tile map example, the lighting examples, post processing and
the big one (`town`, everything in one scene with a blob to walk about) all load those. `World.AddLights` is the
one place a `light_radius` property becomes a `Light2D`. The UI examples are three, a HUD and a panel in C# and
as a script file, a settings screen out of a layout file, and a gallery of every control with a page each, all
from `Assets/examples/ui`.

The checks are the old self tests, always driving themselves now (`ISelfCheck` says when one is done and how it
went), their maps and layouts the exact files the old code used to write, in `Assets/examples/checks`. The tile
map check's numbers are the numbers in those maps. `--checks` ran 135 of 135 on the day.

The host. `TestDefinition` has a level, a summary, what to look for and where the source is, and the selector
shows those next to the list for whichever row the pointer or the navigator is on. The strip over an example
has previous and next on it (Page Up, Page Down) and the keys are on caps. A performance overlay on the engine
itself for every example looked like it couldn't be had, whatever it makes on the GPU the first time it is drawn
would count as the running example's and be freed with it (`ReleaseSince`). It can, `PerformanceOverlay` makes
everything of its own under `AssetScope.EnterGlobal()` now (its `Initialize` and its `Render`, a skin stitches
its atlas as it is first drawn), which is what a thing that outlives every scene should have been doing all
along, Fighter2D's was one scene change away from the same hole. It starts out as the one line (`Compact` is the
default of the constructor now) with the key in yellow next to the numbers, just "F3", a word more had to be too small to read, which the overlay writes
itself out of its `ToggleKey`, so every game that adds one gets the hint. The host puts it bottom right.

`docs/metrics/raw/run_scenes*.py` have the new ids. `run_validation.py` and the workbook makers still say the
old ones, they read numbers that were taken under those names, and the old ids are aliases in the catalog, but
`tilemap` is the example now and `check-tilemap` the checks.

## The Skyline debugger

bogz asked for a built in debugger, a menu bar, a scene tree, a content browser, the game in a container with
an inspector that sets fields as it runs, there in Debug and cut out of Release. It is `Engine/Debugging`, all
of it UIX in the flat skin, built in code rather than out of a layout file so there is nothing of it to ship.

How it sits in the engine. The engine adds it to itself before the scene manager (so it has had its say about
the mouse and keys before a scene reads them) but its `Render` does nothing in its turn, the engine calls
`Debugger.Draw` after everything else, whatever was added to the engine later. Up, `Draw` blits the window into
a render target (`CopyWindow`, the one the transitions use) and the dock paints over the whole window with that
picture in the middle. No redirecting of `BindWindow`, no second viewport size for the game to trip over, the
game never knows. The compositor of the suite has `Retained` off, the picture changes every frame without a quad
of the UI changing and a retained UI would show the same frame of the game for ever. First idea was three
states (hidden, bar only, panels over or around the game), bogz wanted two, hidden or the lot with the game
only ever in its container, on F10. The examples host had its VSync check on F10, that is F11 now.

The dock (`DebugDock`) hands every part its rectangle in `Arrange`, nothing in there has a size of its own, a
`ScrollPanel` has no height unless somebody gives it one and this is the somebody. The tree, the asset grid, the
metrics and the log are each one component that paints its own rows and only the ones in view, a button a row
would have been thousands of quads for a scene with a few hundred sprites in it.

The inspector is reflection (`Inspectable`, tested in Horizon.Tests under `#if DEBUG`). Rows are made when
something is selected and read again every 0.12 s. It all runs in the UI update on the simulation thread, the
same thread and the same moment between ticks the game would set the field itself, so setting is as safe as the
setter is. A struct is a copy in a box, a level that opened one writes the box back to where it came from after
every change. `Entity.Tweens` and `UIComponent.Tweens` and `Object` are hidden from it, they make something the
first time they are read.

Things that bit or nearly did. `Mouse.Position` and friends became computed from `WindowPosition` so the game
can be handed the pointer as its container has it. A texture can be destroyed by the render thread while a draw
list that names it is on its way, `SpriteTexture.Index` checks `IsValid` so that is a blank and not a crash, and
the suite keeps a game picture of the wrong size eight frames before disposing of it. Only what is sampled and
holds colours gets a thumbnail (`SkylineDebugger.CanShow`), not depth and not the whole number grids.

Not done and worth doing next. Undo, writing tuned values back to wherever they came from, clicking something
in the game to select it, dragging the panel edges, a timeline for the passes rather than the last frame.

## The UI was a tick behind, and the clock took five seconds to wake up

bogz saw the UI drawing behind in the pacing example and wanted a cursor drawn by the UI that doesn't lag. The
pacing example got a tag, a UI panel the scene puts on the heart every update, and counts how far it trails in
frames (everything in there reads in frames now, not percent). It trailed by exactly one tick, 28 frames at
3400 fps. A compositor is a component of its scene and components update before the scene's own `UpdateState`
body, so the UI was painted before the scene moved anything. Painting moved to `Capture`, the end of the tick,
for any compositor that is captured (one that never is, set up with the simulation standing still or driven by
hand, still paints in its update, `capturedBefore`). The tag is 0.00 behind now. All 135 checks and Hex's 97
still pass, nothing in them minded `Bounds` being a tick old straight after the update.

Second thing, lists that weren't the same quads from one tick to the next weren't blended at all, the older one
was shown for the whole tick. One label with a changing number and the whole UI moved in tick steps.
`BlendByKey` matches quad to quad by `Key` through a dictionary when the lists differ. That made an old glitch
louder, a menu is one owner and its quads are counted in order, so the hover highlight shifted every later
quad's key by one and the separator rule blended with the highlight next to it (bogz saw it in Hex's menus).
`UIDrawList.BeginPart(i)` keys a row at a time, MenuBar, ContextMenu, Dropdown, ListBox, TabPanel and the
Skyline debugger's own rows use it. Anything new that paints rows with a highlight wants it too.

The pacing readings took ten seconds to settle and that was not the meter. `SnapshotClock` took the first gap
between two publishes as its tick interval, which is the first scene being set up, a tenth of a second, so
frames were drawn way in the past, before the oldest of the three ticks it keeps, standing on that tick and
stepping once a tick, while the shown moment crept forward at the 5 percent it is allowed. The window manager
tells the clock its interval now (`NominalInterval`), gaps are clamped around it, and a moment that falls before
the oldest tick goes straight to where it belongs (it was standing still, that is the hitch already). Then the
steady state turned out to be bad as well, the lag eased down towards the last tick's lateness, settled near the
average, and one tick in seven came later than that with the frames waiting on the newest tick for it. It holds
the worst of the last 128 ticks now. Zero frames still from the first reading on. This costs whatever the wake
up jitter is in delay, a millisecond or so here, which is the right trade.

The cursor. Everything interpolated is a tick and a bit behind by design and the mouse is read once a tick on
top, fine for a menu and hopeless for a pointer. `UICursor` on a compositor is drawn in `Render` from
`Mouse.LivePosition` (the packed position the window thread writes, read atomically), over the UI, with a batch
of its own made the first time. A gamepad cursor hands its position out through `UICursor.Position`, which is
called on the render thread, so it has to be something that can be read from there.
