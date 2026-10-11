# AutoVoxel, the plan for survival

What AutoVoxel has to grow before survival is playable, then complete, then fun, in the order it has to be built.
Written after the rebuild of October 2026, when it became a path traced world that is all on screen at once. Every
item says whether it's the engine's (Horizon, which every game gets) or the game's, how big it is (S an afternoon,
M a few days, L a week or more), and what it waits on.

## Where it stands

There's a world and you can be in it. A finite continent of up to 8192 by 8192 blocks with mountains, rivers,
eleven biomes, caves with lava at the bottom, ore veins, five kinds of tree and ten kinds of little 3D foliage. All of
it is path traced every frame out of one sparse 64-tree (the sun with soft shadows, light that bounces as often as it
likes, water that reflects and refracts, sky, clouds, haze, bloom, TAA, eyes that get used to the dark) at 90 to 160
frames a second at 1080p on an RTX 3060. You walk, sprint, crouch, jump, swim and fly through the engine's own
character controller, break and place blocks from a hotbar, and save and load. There's a menu, options and a
day that goes round.

That's creative mode. What survival needs on top of it is a reason to do anything, which means something to lose
(health, hunger, the dark), something to get (items, tools, a base), and something in the way (monsters, depth,
distance). Everything below is in service of those three.

## The two engine pieces everything waits on

Most of the survival list is game code, but two things only the engine can do, and almost everything else needs them.
They come first.

**Things that aren't blocks, in the path tracer** (engine, L). Right now the only thing the tracer can see is the
voxel tree. Dropped items, mobs, arrows, the player's own hand and the block in it, a falling block, all of it needs
drawing, with the same light and shadows as the world. The plan is voxel models (16 to 32 a side, a few colours, made
the way the foliage models are, or out of Aseprite files) placed anywhere, turned any way, each in a box of its own.
Once a frame the boxes are binned into a coarse grid of the screen (or a small BVH of a few thousand boxes, rebuilt
every frame on the GPU, it's cheap at that count). The eye rays and the shadow rays test the boxes their pixel or
their path crosses, go into the model's own grid in its own space, and keep whichever hit is nearer, the world's or
the model's. The bounce light can leave them out to start with. Animation is swapping models (a few frames of a walk)
or models in parts (a body, a head, four legs, each its own box), which is how Minecraft does it and what reads best
in voxels anyway. Without this there is nothing to fight and nothing to pick up, so it is the first thing to build.

**Light the game can ask about** (engine, M). Monsters spawn in the dark, crops grow in the light, a torch keeps a
room safe. The path tracer knows how lit everything is but only on the GPU, a frame at a time, and only what's on
screen. The game wants a number for any block, now. So the tree gets a light field of its own on the CPU, the
Minecraft way, sky light that comes down until something stops it and block light that spreads out of every emissive
block and drops by one a block, kept per chunk, worked out when the chunk is made and when something in it changes.
Cheap, exact, and exactly what the rules want. It can go to the GPU as well later, see the torches below.

## Phase one, playable

The loop that makes it survival at all. You can die, you have to eat, you dig for things and make better things out
of them, and the night is dangerous.

**Health and dying** (game, S). Health, hits, and death with a respawn at your bed or the spawn. Fall damage off
`CharacterController3D.LandingSpeed` (it's there already), drowning off `IsUnderwater` with a breath bar, burning in
lava. A death screen that says how you died.

**Hunger** (game, S). A bar that runs down with time and faster with sprinting, health that only comes back while it's
full enough, eating to fill it. Without hunger nothing makes you leave the house.

**Items and the inventory** (game, M). An item registry (blocks are items, plus tools, food, materials), stacks, an
inventory of 27 and the hotbar of 9, opened with E, dragging and splitting stacks with the mouse. The UI has panels,
grids and images already, the item icons come out of the atlas like the hotbar's do now. Inventory saved with the world.

**Mining** (game, M). Blocks get a hardness and the tool that's right for them, breaking takes time (cracks drawn on
the block, a decal in the surface pass, or the tile swapped for a cracked one), and a broken block drops its item
(stone drops cobblestone, ore drops ore, leaves sometimes drop a sapling). Needs the engine's entities for the dropped
item to sit there, spin and be picked up.

**Crafting and smelting** (game, M). Recipes in a HIDL file like everything else (`recipes.hor`), a 2 by 2 grid in
the inventory and a 3 by 3 one at a crafting table, a furnace that turns ore into ingots with fuel over time. The
tool tiers follow, wood, stone, iron, diamond, each faster and able to mine the next ore. Furnaces and chests mean
blocks with something inside them, so the world gets **block entities** (engine, S), a dictionary from a voxel to an
object, saved with the tree.

**Monsters at night** (game, L, after the engine's entities and light). Something that spawns in the dark (block
light and sky light both low), walks to you, hits you and burns or wanders off in the morning. Then a second kind that
keeps its distance (an archer) and one that does something you have to deal with differently (explodes, climbs,
poisons). Melee with knockback and a moment of being untouchable after a hit. They need **pathfinding in a voxel
world** (engine, M), A* over blocks you can stand on with jumping a block and falling a few, and the engine's tile
map pathfinder is the 2D half of that already.

**Animals and food** (game, M). Something passive to hunt (and later to farm), cooked meat worth more than raw. Same
entity and pathfinding pieces as the monsters, less to go wrong.

**Torches** (engine, M, then game, S). A torch is the first thing anybody crafts. Emissive blocks light the world now,
but only through the bounce rays, and a small light seen by one ray in fifty is noise that takes a second to settle.
For a lot of little lights at night the tracer has to go looking for them, next event estimation, every pixel picking
a light that's near it out of a grid of the emissive blocks (built from the CPU light field above, the cells that have
block light in them) and sending one shadow ray to it. That's what makes a torch lit room look like one, steady and
crisp. With it, the torch itself is a small model on the side of a block, like the foliage.

**Saving more than blocks** (game, S). The player (health, hunger, inventory, spawn point), the block entities, the
mobs that are about, the time. Autosave every few minutes on a thread of its own (the tree saves a 2048 world in under
a second, an 8192 one in about eight). A list of worlds on the menu instead of just the last one.

At the end of phase one you can start with nothing, punch a tree, make tools, dig, smelt, build a shelter, light it,
and survive the night. That's the game.

## Phase two, complete

The world reacts and there's somewhere to go.

**Water and lava that flow** (game, M, with an engine helper, S). Water and lava are still blocks that stay where they
were made. Flowing is a cellular automaton over the blocks round the player, a few steps a second, written into the
tree with `SetMany` a batch at a time (an edit rebuilds the chunk it's in, about a millisecond, so batching matters).
Levels below a full block want the water's surface drawn lower, which the surface pass can do from a level kept in the
voxel. Buckets, and obsidian where they meet.

**Things that change by themselves** (game, M). Random ticks over the blocks near the player, grass spreading, leaves
falling off when their tree is cut, saplings growing into the trees the generator grows (`WorldGenerator`'s tree
methods take a writer, they can grow into the tree as well as into a chunk), crops, snow and ice. Only near the
player, the whole world is loaded and the whole world can't be simulated, so the game keeps a ring of active chunks
round whoever is playing.

**Farming** (game, M). Tilled ground, seeds, crops through their growth stages (foliage models, one per stage),
water nearby to grow, harvests. Breeding animals.

**Places to find** (game, L). The world has nothing built in it. Villages, dungeons with a spawner and a chest,
mineshafts, ruins on the coasts, towers on the peaks. Every one written into chunks by the generator the way trees
are (worked out from the seed and where they are, so any chunk can be made on its own), with chests filled from
loot tables. And because every one of them can be seen from kilometres away (it's all on screen, that's the point of
this engine), they're what gives a world this size a reason to cross it. A tower on a far peak, lit at night, is a
quest by itself.

**Sound** (engine, M, then game, M). There is none. The engine wants an audio system (OpenAL through Silk.NET, the
same way it has everything else), sounds placed in the world and heard from where you are, and music. The game wants
footsteps by material, breaking and placing, mobs, water, wind, rain, and something quiet playing now and then.

**Armour and better tools** (game, S). Armour from the same materials as the tools, a little less damage each tier.
A shield, a bow and arrows (entities again).

**Death that costs something** (game, S). Your items dropped where you died, a while to get back to them.

## Phase three, fun

What turns it from complete into something you come back to.

**Weather** (engine, M, game, S). The clouds are there and their cover is a setting. Rain (streaks in the surface
pass, wet surfaces a bit darker and glossier, ripples on water), storms with lightning that lights the whole world for
a frame (the path tracer does that for nothing, the flash is a light like any other), snow in the cold biomes.

**The night sky and the long view** (engine, S). The moon goes through its phases, the stars turn. Distant lights at
night, every village and every torch you've placed glowing on the horizon, already works, it wants showing off.

**Something to aim at** (game, L). A goal deep down or far away. A place under the deepslate that's hard to reach and
harder to survive, with a boss and something worth having at the end of it. Advancements as the trail of breadcrumbs
to it, so nobody has to read a wiki.

**Building that's worth doing** (game, M). Slabs, stairs, fences, doors, glass panes, coloured blocks, lamps of different
colours. Slabs and stairs are blocks with models in them, like the foliage but solid, the controller already walks
up a half block. Doors are blocks with a state. The path tracer makes anything built look good, so more to build with
is more fun straight away.

**A map** (game, S). A map of the whole world is a picture of its top, which the tree has, every node knows its
colour from above. Reading the colour of the node of the right size for every pixel of the map is a few lines.

**Modes** (game, S). Creative (what it is now), survival, hardcore (one life), peaceful. A difficulty setting.

## What the engine should get on the way, for speed

Measured this week on an RTX 3060, at 1600 by 900, a frame of a 2048 world is between 4.7 and 8.3 ms, at 1080p
between 6.4 and 11.3, and a 4096 or an 8192 world costs the same as a 2048 one to draw. That's plenty for survival as
it is. Where to go next, if it's needed.

- The eye rays still cost 1 to 3 ms. A coarse pass at an eighth of the size that finds how far each block of pixels
  can go before it could hit anything (a cone trace through the same tree) and starts the full rays there would take
  most of the empty sky and air out of it.
- The sun's shadows at sunset (the sun low, the shadow rays long and skimming over everything) are up to 2.3 ms.
  They could be traced at half size with the TAA filling in, or kept from frame to frame while the sun hardly moves.
- Memory. An 8192 world is 2.4 GB, most of it the voxels of the smallest nodes that aren't all one thing (where
  dirt meets stone, ore veins, cave walls). Most of those hold only two materials. A node of two materials kept as a
  second mask and the two of them, rather than a byte for every voxel, would take that to about a fifth.
- Changes rebuild the 64 a side chunk they're in (a millisecond or two) and leave the old nodes behind until the next
  save. For explosions, flowing water and everything else that changes a lot of blocks, the tree wants compacting
  now and then on a thread of its own while the game goes on.

## In what order

1. Entities in the tracer, and the CPU light field (engine).
2. Health, hunger, items, inventory, mining with drops (game).
3. Crafting, smelting, tool tiers, block entities.
4. Pathfinding, then monsters and animals.
5. Torches done properly (light sampling in the tracer).
6. Saving everything, autosave, the world list.

That's survival you can play. Then flowing water, random ticks, farming, structures and sound make it complete, and
weather, a goal, more to build with, a map and the modes make it fun.

Every step can be checked the way the rebuild was. `AutoVoxel --play 2048 --tour shots` goes round a world and
takes screenshots, it's the place to add a stop for whatever was just built (a monster chasing the camera, a furnace
lit, a lit room at night), and `Horizon.Tests` has the tree, the controller and the colliders under test already.
