# How the path traced lighting works

The "fancy" lighting of a `DeferredRenderer2D` is radiance cascades (Alexander Sannikov's idea, from Path of Exile 2),
done for a flat world in compute. This is the whole of it, written down because it is one of those things that is
obvious once it clicks and a wall before. The code is `Rendering/2D/Lighting/PathTracedLighting2D.cs` and the three
shaders `gi_radiance.slang`, `gi_cascade.slang` and `gi_resolve.slang` under `shaders/lighting`.

## The question it answers

For every pixel of the picture, how much light arrives there from every direction that did not come straight from a
lamp. The lamps themselves are the deferred pass's job (`direct.slang`, with their shadows). What is wanted on top is
the bounce, the light a wall throws back after a lamp hits it, and what the glowing things throw out with their own
shape. Both of those are "look around you from this pixel, and whatever you see, that is light arriving".

Looking around from every pixel in every direction is a ray march per pixel per direction. For 800 by 450 pixels and
64 directions that is 23 million marches a frame, and the picture would still be noisy unless there were a lot more.
Cascades are the trick that makes the same thing cost a few hundred thousand short marches and come out with no noise.

## The observation

Two facts about light arriving at a point.

Light from near by changes quickly from one point to the next (step past the corner of a wall and a lamp appears) but
does not need many directions (a thing that is near covers a wide angle, a few rays find it).

Light from far away needs many directions (a lamp far off is a sliver of a degree, a ray has to point right at it) but
barely changes from one point to the next (take a step and the far lamp is in the same place).

So near light wants probes close together with few rays, far light wants few probes with many rays. One grid of probes
cannot do both well. A stack of grids can, if each grid only looks at the distances it is good for.

## The cascades

Cascade 0 is a probe every 2 pixels of the picture. Each probe sends out 4 rays, each over the first 4 pixels away
from it, and no further. Cascade 1 has a probe every 4 pixels, 16 rays each, over the distances 4 to 20 pixels.
Cascade 2, every 8 pixels, 64 rays, 20 to 84 pixels. And so on, every cascade up has a quarter of the probes, four
times the rays, and an interval four times as long starting where the last one stopped. `Describe` in the C# works
those numbers out, `BaseInterval` and `ProbeSpacing` are the knobs.

The arithmetic that makes it affordable is that a quarter of the probes times four times the rays is the same number
of rays in every cascade, so every cascade costs the same and the whole stack costs a handful of times one cascade.
Five or six cascades reach across the whole picture.

A ray is a march through the signed distance field of what blocks light (the occlusion map's field, and the sprite
field with its "glow" channel so that what is a lamp also stops rays). It steps by the distance the field says is
safe and stops when it gets within a quarter of a world unit of something. The texels of a cascade's texture are its
rays, laid out with the direction as the outer grid and the probe as the inner one, so a cascade is one texture about
the size of the picture.

## Merging, which is the part that is hard to get right

A ray of cascade 0 only looks 4 pixels out. What is beyond that is cascade 1's business, and cascade 1 has four rays
for every one of cascade 0's (sixteen directions where cascade 0 had four, so each of cascade 0's directions is a
cone that cascade 1 splits into four narrower cones). So a ray of cascade 0 that hit nothing in its 4 pixels takes the
average of the four rays of cascade 1 that continue in its cone, from the four probes of cascade 1 nearest to its
own probe, blended bilinearly by how near each is. A ray that hit a wall takes what the wall threw back and nothing
from above, the wall is in the way.

That is why the cascades are built from the top down, `for (int i = cascadeCount - 1; i >= 0; i--)` in `Run`. The
furthest cascade has nothing above it and holds what its own rays found. Every cascade below merges the one above
into itself as it is made, so by the time cascade 0 is done every one of its rays carries the light from the whole
of its cone all the way out to the edge of the picture, gathered at the resolution each distance deserves.

The alpha of a ray is how much of what is further away still gets through. 1 for a ray that hit nothing (take all of
the cascade above), 0 for one that hit a wall (take none). In `csMain` of `gi_cascade.slang`:

    found.rgb += found.a * above.rgb;
    found.a *= above.a;

The two mistakes everyone makes here. One, merging by the ray's own direction index in the cascade above instead of
the four rays it splits into (direction d of this cascade continues as directions 4d to 4d+3 of the one above, see
`upper`). Two, letting a ray that hit a wall also take the light from above, which leaks light through walls.

## Resolving

`gi_resolve.slang` turns cascade 0 into a picture. For every pixel it takes the four probes of cascade 0 around it,
blends them by how near each is, and averages their four rays. The average over every direction of the light
arriving is the irradiance at that pixel, which is what a flat surface receives. That texture is what the deferred
pass adds to the direct light (`uTexGi`), scaled by `Strength`, with the ambient scaled down to make room for it.

There is no noise in one frame of this. Nothing is random, every probe sends the same rays every frame. What there
is instead is a little softness and a little light leaking round thin walls, which is the price of the bilinear
blends, and spokes round anything that glows and is only a few pixels across, which is the price of rays a fixed
angle apart. See "Over frames" further down for what is done about the spokes.

## What a wall throws back

When a ray lands on a wall it needs to know how bright that spot of wall is. That is the wall's colour times the
light falling on it, the lamps (with shadows) plus the bounced light the tracer found there last frame, plus
whatever of it glows. Working that out in every ray is the whole deferred pass over again hundreds of thousands of
times, so `gi_radiance.slang` does it once a frame for every texel of the small picture that is on or next to
something solid or glowing, and the rays read that texture at the point they hit. Reading last frame's result there
is what makes light bounce more than once, each frame carries the last frame's bounce one step further, and
`Bounce` is how much a wall keeps, under 1 or it feeds on itself.

## The probes are pinned to the world

The probes used to sit on the picture, so when the camera panned a pixel every probe saw a slightly different world
and a lamp a few pixels wide got hit by a ray one frame and missed the next. The lanterns breathed. Now every
cascade's grid sits on multiples of its spacing in world units and the picture is offset to that (`Offset` in the
C#, `uProbeOffset` in the shaders), with one probe more each way to cover the edge. Each grid is a grid of the one
above it, so the merges still line up.

## Over frames

Pinning the probes to the world was not the end of the flicker. A ray stops at whatever the field says is in the
way, and things that glow (a lava tile, a mushroom, an ember) are in that field by way of the sprite field, which
is a jump flood of the picture made every frame (`SpriteShadows`). The picture moves under the world with the
camera, so the field of a mushroom three pixels across comes out a little different at every quarter of a pixel
the camera has crept, and the few rays that graze it hit it on one frame and miss it on the next. Standing still
the dungeon's cave was as steady as anything, drifting at three world units a second the light round every
mushroom swapped sides from frame to frame. Measured on the picture of the traced light alone, in blocks of 20
pixels, the second difference of their brightness over time was 0.12 standing still and 2.37 drifting, with
nearly half of the blocks jumping by more than 2.

So every frame is laid over the ones before (`Accumulation`, a tenth of a second by default). The resolve reads
last frame's result where the camera has moved it to (`uShift`, the same shift the bounce has always been read
with). Every pixel holds on to so many seconds' worth of light (the alpha of the result, up to `Accumulation`) and
a frame of `dt` seconds goes in by `dt / (held + dt)`. A pixel that has only just come into view catches up in a
few frames, one that holds all it may is an average over the last tenth of a second at any frame rate. It used to
be counted in frames, and every slow frame (a hitch, or the screenshots of the measuring) cut the count of every
pixel down to what that one frame allowed, which then took dozens of frames to climb back, the light was under
smoothed for most of the time and measured 0.73 to 0.98 where it should have been 0.45. The share is never less
than a fiftieth, the result is half floats and a smaller share rounds away to nothing, the light stops a few
percent short of where it is going and stays there. No neighbourhood clamp, the usual cure for ghosting. The flicker
is spatially smooth (the whole glow round a mushroom goes up or down together), so a clamp to what is round a pixel
this frame lets it straight back through. Instead every pixel keeps the mean of its brightness and of its square
over the same time, so it knows how much its light usually wavers, and a change past three times that plus half the
light itself is something that happened (a brazier catching, a spark arriving) and the pixel lets go of all but a
frame's worth (`Responsiveness`, 0 for never). It measured a touch steadier than without, 0.40 against 0.45
standing still. That it shortens the tail behind something moving is the idea and is not measured, the one try at
it (the lantern switched on mid-run) was drowned by the brazier and the runes in the same room. A cut (the camera jumping more than half a picture),
the first frame, and a frame that comes a quarter of a second after the last one (the tracing was off) start from
nothing, and so does the bounce of the radiance pass, which used to read whatever the texture had in it on the very
first frame.

That alone took the drifting jump from 2.37 to 0.34 and standing still to 0.03. What it doesn't do is anything
about the spokes, they are the same every frame so they are accumulated as they are. That is the second half, the
rays are turned a little every frame (`Jitter`, `uRotation` in `gi_cascade.slang`). Every cascade is turned by the
same angle, which is the one thing that keeps the merge right, a ray of one cascade carries on into the four rays
above it, and those only stay inside its cone if the whole lot turns together (each cascade turned by its own
fraction of its own cone puts the four next to the cone and not in it). The angle goes round by the golden ratio,
somewhere within a cone of the nearest cascade (a quarter turn), so any handful of frames in a row are spread over
the whole of it. A rigid turn by any angle is the same rays relabelled, the sub-cone each cascade ends up with is
every bit as spread out. Over a few frames every direction has been looked in and the light round a small lamp is
round. The price is a little shimmer, 0.3 to 0.45 standing still where it was 0.03, most of it at sixty frames a
second where a tenth of a second is only six frames. It stays on, for what it does with fewer cascades.

## Upscaling, tried

The idea was to trace small and keep the light at the renderer's size, moving the whole probe grid by a fraction of
a probe every frame (Halton in two and three, the same distance for every cascade so the merges don't notice) and
having a pixel take most from the frames that had a probe near it (a bell over the distance, 0.35 of the spacing,
normalised so the light settles as quickly on average). Over a few frames every pixel has been looked at from nearly
where it is. On the dungeon's cave it didn't pay. Traced at a quarter size it came out a bit more shapely than a
quarter size without it and nowhere near half size, and it was less steady at every size, 0.6 to 0.9 against 0.4
to 0.5, with a wider bell (0.5) no better. What it doesn't touch is the picture the rays read the brightness of
what they hit from, the radiance of `gi_radiance.slang`, which is still the traced size, and at a quarter a mushroom
is a texel of it. Where a ray stops is another matter, that is the sprite field, which is the full size of the
screen whatever the tracing is. Which of the two limits the detail more, the radiance picture or the probes being
further apart, was never pulled apart, keeping the radiance picture at half while the cascades are traced at a
quarter is the test that would say (it is read by world position, nothing ties its size to the cascades'). Moving the radiance picture about with the probes (worked out a fraction of a texel off the middle
of each texel and read back with the same fraction) is the obvious next step and the one try at it drew a fine
dotted grid over everything and left the mushrooms dark in the middle, both sizes, so it came out again. Whoever
picks this up wants every picture that is read by the texel moved by the same sub-texel distance and read back
consistently, and to look at the raw cascade textures before trusting the result. `Upscaling` is
there, off, with the resolve doing its half of it (the light at the renderer's size, the bell).

## Fewer cascades

Every cascade costs about as much as the next, so how many there are is what the tracing costs. On the dungeon at
1600 by 900 (five cascades at half size) the tracing was 1.6 ms of the GPU's frame, 1.2 with four, 0.7 to 0.9 with
three and 0.45 with two. The furthest cascade used to stop at the end of its interval, so fewer cascades was less
light, a lamp further off than the last interval reaches was simply not seen. Now the furthest one looks all the way
to the far corner of the picture whatever, and fewer cascades is light from far off looked for with fewer rays.
Without the jitter that is blotches, at two cascades a mess. With it, three cascades are hard to tell from all of
them and jump about by 0.6 drifting. Two still go blotchy and shimmer about as much as the old flicker did, the far
cascade has sixteen rays for the whole picture, and no amount of frames makes that up. `MaxCascades` can be changed
while the game runs (the cascades are made again on the next frame), Fighter2D has it as Light quality on its
options screen, Low being three, Medium four and High all of them, and the dungeon example goes round them on Q.

## Where the time goes

Measured on the dungeon's cave and the cellar at 1600 by 900 (five cascades at half size, about 1.5 ms of GPU),
with a scope round every dispatch for an afternoon. The wall radiance is 0.05 ms, the resolve 0.03, everything
else is the cascades, and not evenly. Cascade 0 is 0.08 ms, 1 is 0.19, 2 and 3 are 0.3 to 0.7 and 4 is 0.25 to
0.45. "Every cascade costs the same" is true of the ray count and false of the rays. A far probe is sixteen or
thirty two pixels from the next, so the threads of a workgroup sample the fields far apart from each other and
the cache gets nothing out of it, where cascade 0's probes are two pixels apart and every sample is next to the
last. The marches themselves are sphere traces, so the step is the distance to the nearest thing, and in a cave
of mushrooms or a cloud of sparks the nearest thing is always a pixel or two off.

What was tried and what it came to, so nobody does it again expecting different. The world to picture mapping per
step was a 4x4 matrix multiply and is an affine scale and offset now (worked out once per thread in `makeField`,
`pictureOfFast`), the walls' field lookup divides once per thread instead of per step and only takes a square
root when the point is off the map, the merge with the cascade above reads its four probes as one hardware
bilinear sample per direction instead of four loads, and the square roots of the direction counts come in as
uniforms. All correct, all cheaper on paper, none of it measurable against the frame to frame noise, which tells
you the pass is bound on texture reads and not on arithmetic. Letting the far cascades take bigger minimum steps
(half a pixel doubling from cascade 2) didn't move the numbers either and would have let a far ray step over a
spark, so it came out again. The things that do move the numbers are the ones a game can set, `MaxCascades`
(three is about half), `Scale` and `ProbeSpacing`. The next real thing to try is the thread layout of the far
cascades, so a workgroup's rays sample near each other, or a lower resolution copy of the fields for them.

## If you are implementing it yourself

Get cascade 0 alone working first, with the interval reaching across the whole picture and no merging, and look at
the raw cascade texture. You should see the picture repeated in a grid of directions. Then add cascade 1 and the
merge, with the intervals butting up against each other, and check that a lamp lights the same with two cascades as
it did with one long one. The merge is where it goes wrong, and it goes wrong quietly, as light that is a bit too
dim or a bit leaky. Build from the top down. Keep a probe inside a wall from seeing out of its back (`leaveWall`),
or every floor lights its own ceiling.
