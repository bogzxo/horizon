# How the path traced lighting works

The "fancy" lighting of a `DeferredRenderer2D` is radiance cascades (Alexander Sannikov's idea, from Path of Exile 2),
done for a flat world in compute. This is the whole of it, written down because it is one of those things that is
obvious once it clicks and a wall before. The code is `Rendering/Lighting/PathTracedLighting2D.cs` and the three
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

There is no noise in this. Nothing is random, every probe sends the same rays every frame, so there is nothing to
smooth over time and nothing to reproject. What there is instead is a little softness and a little light leaking
round thin walls, which is the price of the bilinear blends, and a look that is steady.

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

## If you are implementing it yourself

Get cascade 0 alone working first, with the interval reaching across the whole picture and no merging, and look at
the raw cascade texture. You should see the picture repeated in a grid of directions. Then add cascade 1 and the
merge, with the intervals butting up against each other, and check that a lamp lights the same with two cascades as
it did with one long one. The merge is where it goes wrong, and it goes wrong quietly, as light that is a bit too
dim or a bit leaky. Build from the top down. Keep a probe inside a wall from seeing out of its back (`leaveWall`),
or every floor lights its own ceiling.
