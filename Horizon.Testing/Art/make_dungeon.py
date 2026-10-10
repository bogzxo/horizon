#!/usr/bin/env python3
"""Paints the dungeon of the dungeon example and writes its map, into Assets/examples/dungeon.

usage  python Art/make_dungeon.py          from Horizon.Testing, whenever a tile, a sprite or the map in here changes

What comes out, all 16 pixels a tile.

  tiles_albedo.png     the floor, the walls, the lava and everything that glows
  tiles_normal.png     which way their surface faces, worked out of how high every texel is
  tiles_specular.png   how shiny, which is the puddles and the lava and not much else
  tiles_ao.png         how hemmed in, out of the same heights
  tiles.tsx            the tile set, with the two frames of the lava and of the pool of light
  sprites.png          the slime, the crystal, the spark, the brazier, the gate and the crate, a grid of 16 by 16
  dungeon.tmx          the map, five rooms and what is in them

The map is carved out of solid rock a rectangle at a time and furnished by where things go, both further down.
It is seen from above. What glows is on layers that say so (Emissive), and to the path traced lighting those are
lamps, the lava lights its room red and a mushroom lights the wall behind it, nobody put a light there.
"""
import math
import os
import sys

import numpy as np
from PIL import Image

T = 16          # texels a tile
COLUMNS = 8     # tiles a row of the sheet
ROWS = 3

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "Assets", "examples", "dungeon")

TAU = math.pi * 2.0


def noise(x, y, seed=0):
    """A number from 0 to 1 that looks random and is the same for the same texel every time."""
    h = (int(x) * 374761393 + int(y) * 668265263 + seed * 2147483647) & 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0


def mix(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def shade(colour, by):
    return tuple(max(0.0, min(255.0, c * by)) for c in colour)


# Every tile painter returns (colour, alpha, height, shine) for a texel, or None for nothing there


def floor(x, y, variant=0):
    # Flagstones, four to a tile, their joints a texel deep. Every kind has its joints in the same places so
    # they run on from one tile into the next, what differs is the stones
    sx, sy = x % 8, y % 8
    if sx == 0 or sy == 0:
        return (24, 26, 34), 255, 0.0, 0.0

    stone = x // 8 + (y // 8) * 2 + variant * 4
    base = mix((58, 62, 78), (88, 92, 110), noise(stone, 5, 21))
    edge = min(sx - 1, 7 - sx, sy - 1, 7 - sy)
    height = min(1.0, 0.5 + edge * 0.22)
    return shade(base, 0.9 + 0.2 * noise(x, y, 31 + variant)), 255, height, 0.04


def cracked(x, y):
    found = floor(x, y, 3)
    if 2 < y < 14 and abs((x - 3) - y * 0.6) < 0.8:
        return (18, 20, 26), 255, 0.0, 0.0
    return found


def bridge(x, y):
    # One slab the width of a tile, with a rim, what is laid over the lava
    if y in (0, 15):
        return (30, 30, 38), 255, 0.2, 0.0
    if x % 8 == 0:
        return (40, 40, 50), 255, 0.4, 0.0
    base = mix((92, 88, 96), (112, 106, 112), noise(x // 8, 2, 77))
    return shade(base, 0.9 + 0.2 * noise(x, y, 79)), 255, 0.9, 0.06


def wall(x, y, variant=0):
    # Big blocks, two courses a tile and every other one shifted by half a block
    course = y // 8
    bx = (x + (8 if course % 2 else 0)) % 16
    by = y % 8
    edge = min(bx, 15 - bx, by, 7 - by)
    if edge < 1:
        return (14, 14, 20), 255, 0.0, 0.0

    which = course * 3 + (x + (8 if course % 2 else 0)) // 16 + variant * 5
    base = mix((44, 46, 60), (68, 70, 88), noise(which, course, 41 + variant))
    if variant == 2 and noise(x // 2, y // 2, 9) > 0.6:
        base = mix(base, (44, 96, 64), 0.7)

    return shade(base, 0.9 + 0.2 * noise(x, y, 43)), 255, min(1.0, 0.4 + edge * 0.3), 0.0


def pillar(x, y):
    # A column seen from the top. Round, and the corners of its tile are nothing, so it blocks light as a circle
    d = math.hypot(x - 7.5, y - 7.5)
    if d > 7.3:
        return None
    if d > 6.3:
        return (18, 18, 26), 255, 0.2, 0.0

    base = (100, 102, 120) if d <= 4.2 else (76, 78, 96)
    return shade(base, 0.92 + 0.16 * noise(x, y, 51)), 255, 1.0 - d * 0.08, 0.1


def flow(x, y, frame):
    """How hot a texel of something molten is, 0 to 1. The same at both edges of a tile, so a lake of it has no seams."""
    t = frame * 4
    a = math.sin(TAU * (x + t) / 16.0 * 2.0 + math.sin(TAU * (y - t) / 16.0) * 1.5)
    b = math.cos(TAU * (y + t) / 16.0 + TAU * x / 16.0)
    return (a + b) * 0.25 + 0.5 + (noise(x, y, 61 + frame) - 0.5) * 0.12


def lava(x, y, frame=0):
    heat = flow(x, y, frame)
    if heat > 0.62:
        colour = mix((255, 196, 70), (255, 240, 150), (heat - 0.62) * 3.0)
    elif heat > 0.4:
        colour = mix((240, 96, 24), (255, 170, 50), (heat - 0.4) / 0.22)
    else:
        colour = mix((120, 26, 14), (214, 70, 20), heat / 0.4)
    return colour, 255, 0.5, 0.3


def pool(x, y, frame=0):
    # The way out, a pool of light. The lava's flow in another colour
    heat = flow(x, y, frame)
    colour = mix((90, 170, 255), (235, 250, 255), heat * 1.25)
    return colour, 255, 0.5, 0.4


def rune(x, y, variant=0):
    # A sign cut into the floor that glows, a ring with a cross in it or a saltire
    d = math.hypot(x - 7.5, y - 7.5)
    ring = abs(d - 5.5) < 0.7
    cross = variant == 0 and d < 4.0 and (abs(x - 7.5) < 0.6 or abs(y - 7.5) < 0.6)
    saltire = variant == 1 and d < 4.5 and abs(abs(x - 7.5) - abs(y - 7.5)) < 0.7
    if not (ring or cross or saltire):
        return None
    return (120, 230, 255), 255, 0.5, 0.0


def mushroom(x, y, variant=0):
    caps = [(4, 6, 2.6), (10, 9, 2.2), (7, 12, 1.8)] if variant == 0 else [(5, 10, 2.4), (11, 5, 2.0)]
    for cx, cy, r in caps:
        d = math.hypot(x - cx, y - cy)
        if d <= r:
            return mix((60, 220, 170), (200, 255, 225), 1.0 - d / r), 255, 0.8, 0.0
    return None


def puddle(x, y):
    # Water lying in a dip, flat and as shiny as anything in here gets
    d = math.hypot((x - 7.5) / 6.5, (y - 8.0) / 4.5) + (noise(x // 2, y // 2, 91) - 0.5) * 0.25
    if d > 1.0:
        return None
    return mix((34, 50, 76), (22, 32, 52), d), 235, 0.5, 0.95


def shards(colour):
    """A growth of crystal on the floor in a colour, three shards of it."""

    def paint(x, y):
        for cx, cy, w, h in [(5, 9, 2.6, 5.5), (10, 7, 2.2, 4.5), (8, 12, 3.0, 2.6)]:
            d = abs(x - cx) / w + abs(y - cy) / h
            if d <= 1.0:
                lit = 1.15 if x < cx else 0.8
                core = mix(colour, (255, 255, 255), max(0.0, 0.55 - d))
                return shade(core, lit), 255, 1.0 - d * 0.5, 0.5
        return None

    return paint


def bones(x, y):
    on = (abs(y - 9) < 1 and 3 <= x <= 12) or (abs(x - 6) < 1 and 6 <= y <= 12 and (x + y) % 2 == 0)
    skull = math.hypot(x - 11, y - 5) < 2.2
    if not (on or skull):
        return None
    if skull and (x, y) in ((10, 5), (12, 5)):
        return (30, 30, 36), 255, 0.3, 0.0
    return (196, 190, 170), 255, 0.8, 0.0


FLOOR, FLOOR2, FLOOR3, CRACKED, WALL, WALL2, MOSSY, PILLAR = range(1, 9)
LAVA, LAVA2, BRIDGE, RUNE, RUNE2, MUSHROOM, MUSHROOM2, PUDDLE = range(9, 17)
SHARDS_BLUE, SHARDS_RED, SHARDS_GREEN, POOL, POOL2, BONES = range(17, 23)

BLUE, RED, GREEN = (90, 170, 255), (255, 84, 92), (110, 255, 150)

TILES = [
    lambda x, y: floor(x, y, 0), lambda x, y: floor(x, y, 1), lambda x, y: floor(x, y, 2), cracked,
    lambda x, y: wall(x, y, 0), lambda x, y: wall(x, y, 1), lambda x, y: wall(x, y, 2), pillar,
    lambda x, y: lava(x, y, 0), lambda x, y: lava(x, y, 1), bridge, lambda x, y: rune(x, y, 0),
    lambda x, y: rune(x, y, 1), lambda x, y: mushroom(x, y, 0), lambda x, y: mushroom(x, y, 1), puddle,
    shards(BLUE), shards(RED), shards(GREEN), lambda x, y: pool(x, y, 0),
    lambda x, y: pool(x, y, 1), bones, None, None,
]

# The ones that run on into the tile next door, their heights wrap round when the normals are worked out
WRAPPING = {FLOOR, FLOOR2, FLOOR3, CRACKED, WALL, WALL2, MOSSY, LAVA, LAVA2, POOL, POOL2}


def paint_tiles():
    width, height = COLUMNS * T, ROWS * T
    albedo = np.zeros((height, width, 4), dtype=np.float64)
    heights = np.zeros((height, width), dtype=np.float64)
    shine = np.zeros((height, width), dtype=np.float64)

    for index, painter in enumerate(TILES):
        if painter is None:
            continue
        left, top = index % COLUMNS * T, index // COLUMNS * T
        for y in range(T):
            for x in range(T):
                found = painter(x, y)
                if found is None:
                    continue
                colour, alpha, high, shiny = found
                albedo[top + y, left + x] = (*colour, alpha)
                heights[top + y, left + x] = high
                shine[top + y, left + x] = shiny

    normal = np.zeros((height, width, 4), dtype=np.float64)
    occlusion = np.full((height, width), 255.0)

    for index, painter in enumerate(TILES):
        if painter is None:
            continue
        left, top = index % COLUMNS * T, index // COLUMNS * T
        wraps = index + 1 in WRAPPING
        tile = heights[top:top + T, left:left + T]
        there = albedo[top:top + T, left:left + T, 3] > 0

        def at(x, y):
            # Past the edge of a tile is the other side of it if it tiles, and more of the same if it doesn't
            if wraps:
                return tile[y % T, x % T]
            x, y = min(max(x, 0), T - 1), min(max(y, 0), T - 1)
            return tile[y, x] if there[y, x] else 0.0

        for y in range(T):
            for x in range(T):
                if not there[y, x]:
                    normal[top + y, left + x] = (128, 128, 255, 0)
                    continue

                # Downhill is where the surface faces. The picture counts its rows down and a normal map has Y up
                dx = (at(x + 1, y) - at(x - 1, y)) * 0.5
                dy = (at(x, y + 1) - at(x, y - 1)) * 0.5
                nx, ny = -dx * 1.6, dy * 1.6
                length = math.hypot(nx, ny)
                if length > 0.9:
                    nx, ny = nx / length * 0.9, ny / length * 0.9
                normal[top + y, left + x] = (round(nx * 127.5 + 127.5), round(ny * 127.5 + 127.5), 255, 255)

                # Lower than what is round it is a hole, and how much lower is how deep
                around = sum(at(x + i, y + j) for i in (-1, 0, 1) for j in (-1, 0, 1) if (i, j) != (0, 0)) / 8.0
                hole = max(0.0, around - tile[y, x])
                occlusion[top + y, left + x] = 255.0 * (1.0 - min(0.6, hole * 1.4))

    os.makedirs(OUT, exist_ok=True)
    Image.fromarray(albedo.astype(np.uint8), "RGBA").save(os.path.join(OUT, "tiles_albedo.png"))
    Image.fromarray(normal.astype(np.uint8), "RGBA").save(os.path.join(OUT, "tiles_normal.png"))

    grey = (shine * 255.0).astype(np.uint8)
    Image.fromarray(np.dstack([grey, grey, grey, np.full_like(grey, 255)]), "RGBA").save(os.path.join(OUT, "tiles_specular.png"))
    grey = occlusion.astype(np.uint8)
    Image.fromarray(np.dstack([grey, grey, grey, np.full_like(grey, 255)]), "RGBA").save(os.path.join(OUT, "tiles_ao.png"))


# Every sprite painter returns (colour, alpha) for a texel of its 16 by 16 cell, or None


def slime(frame):
    # A dome with two eyes, squatter on some frames than others, which is all the walking it does
    squash = (0.0, 0.9, 0.0, -0.7)[frame]

    def paint(x, y):
        rx, ry = 6.0 + squash, 5.6 - squash
        cy = 14.0 - ry
        d = math.hypot((x - 7.5) / rx, (y - cy) / ry)
        if d > 1.0 or y > 14:
            return None
        if d > 0.8 or y == 14:
            return (18, 70, 40), 255
        if (x in (5, 6) or x in (9, 10)) and round(cy) <= y <= round(cy) + 1:
            return (14, 38, 22), 255

        body = mix((120, 240, 150), (40, 150, 80), (y - (cy - ry)) / (ry * 2.0))
        if math.hypot(x - 5.0, y - (cy - ry * 0.45)) < 1.5:
            body = (215, 255, 225)
        return body, 255

    return paint


def crystal(frame):
    # A cut stone, white so it can be given a colour, with a glint that comes and goes
    def paint(x, y):
        d = abs(x - 7.5) / 4.6 + abs(y - 8.0) / 6.6
        glint = {1: (10, 4), 3: (5, 11)}.get(frame)
        if glint and (abs(x - glint[0]) + abs(y - glint[1])) <= 1:
            return (255, 255, 255), 255
        if d > 1.0:
            return None
        if d > 0.82:
            return (150, 160, 190), 255

        facet = 1.0 if (x < 7.5 and y < 8.0) else 0.86 if y < 8.0 else 0.74 if x < 7.5 else 0.6
        return shade((238, 242, 255), facet), 255

    return paint


def spark(frame):
    # What gets thrown, a ball of fire, a bit bigger on its second frame
    reach = 3.0 + frame * 0.7

    def paint(x, y):
        d = math.hypot(x - 7.5, y - 7.5)
        if d < 1.6:
            return (255, 250, 224), 255
        if d < reach:
            return mix((255, 214, 110), (255, 150, 50), (d - 1.6) / (reach - 1.6)), 255
        if d < reach + 1.6:
            return (255, 120, 40), int(150 * (1.0 - (d - reach) / 1.6))
        return None

    return paint


def brazier(lit, frame=0):
    # A bowl of coals from above, cold or burning
    def paint(x, y):
        d = math.hypot(x - 7.5, y - 7.5)
        if d > 7.0:
            return None
        if d > 5.0:
            return shade((62, 56, 66), 0.85 + 0.3 * noise(x, y, 101)), 255
        if not lit:
            return ((84, 34, 26) if noise(x, y, 103) > 0.82 else (34, 28, 30)), 255

        heat = 1.0 - d / 5.0 + (noise(x, y, 105 + frame) - 0.5) * 0.5
        return mix((230, 80, 20), (255, 240, 150), heat), 255

    return paint


def gate(x, y):
    # A door of planks with iron across it, shut
    if x in (0, 15) or y in (0, 15):
        return (20, 16, 16), 255
    if y in (3, 4, 11, 12):
        return ((150, 152, 166) if x % 4 == 2 else (92, 94, 108)), 255
    if x % 4 == 0:
        return (52, 38, 30), 255
    return shade((98, 70, 48), 0.9 + 0.2 * noise(x // 4, y, 111)), 255


def crate(x, y):
    # A box, to be shoved about
    if x in (0, 15) or y in (0, 15):
        return None
    edge = min(x - 1, 14 - x, y - 1, 14 - y)
    if edge == 0:
        return (40, 28, 20), 255
    if edge <= 2:
        return (126, 90, 56), 255
    if abs(x - y) <= 1 or abs(x + y - 15) <= 1:
        return (96, 66, 42), 255
    return shade((150, 110, 70), 0.9 + 0.2 * noise(x, y // 3, 113)), 255


SPRITES = [
    slime(0), slime(1), slime(2), slime(3), crystal(0), crystal(1), crystal(2), crystal(3),
    spark(0), spark(1), brazier(False), brazier(True, 0), brazier(True, 1), gate, crate, None,
]


def paint_sprites():
    image = np.zeros((2 * T, COLUMNS * T, 4), dtype=np.uint8)
    for index, painter in enumerate(SPRITES):
        if painter is None:
            continue
        left, top = index % COLUMNS * T, index // COLUMNS * T
        for y in range(T):
            for x in range(T):
                found = painter(x, y)
                if found is not None:
                    colour, alpha = found
                    image[top + y, left + x] = (*[int(c) for c in colour], alpha)

    Image.fromarray(image, "RGBA").save(os.path.join(OUT, "sprites.png"))


def write(name, text):
    path = os.path.join(OUT, name)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as file:
        file.write(text.lstrip("\n"))


def write_tileset():
    write("tiles.tsx", f"""
<?xml version="1.0" encoding="UTF-8"?>
<tileset version="1.10" tiledversion="1.11.0" name="dungeon" tilewidth="{T}" tileheight="{T}" tilecount="{COLUMNS * ROWS}" columns="{COLUMNS}">
 <image source="tiles_albedo.png" width="{COLUMNS * T}" height="{ROWS * T}"/>
 <tile id="{LAVA - 1}">
  <animation>
   <frame tileid="{LAVA - 1}" duration="420"/>
   <frame tileid="{LAVA2 - 1}" duration="420"/>
  </animation>
 </tile>
 <tile id="{POOL - 1}">
  <animation>
   <frame tileid="{POOL - 1}" duration="300"/>
   <frame tileid="{POOL2 - 1}" duration="300"/>
  </animation>
 </tile>
</tileset>
""")


class Grid:
    """A layer as rows of numbers, filled in a tile at a time and written out the way Tiled writes them."""

    def __init__(self, width, height):
        self.width, self.height = width, height
        self.cells = [[0] * width for _ in range(height)]

    def put(self, x, y, gid):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.cells[y][x] = gid

    def csv(self):
        return ",\n".join(",".join(str(cell) for cell in row) for row in self.cells)


def layer(index, name, grid, properties=()):
    says = ""
    if properties:
        says = "  <properties>\n" + "".join(f'   <property name="{key}" type="{kind}" value="{value}"/>\n' for key, kind, value in properties) + "  </properties>\n"
    return (f' <layer id="{index}" name="{name}" width="{grid.width}" height="{grid.height}">\n{says}'
            f'  <data encoding="csv">\n{grid.csv()}\n</data>\n </layer>\n')


def write_map():
    """Five rooms and the passages between them, 56 tiles by 36, cut out of solid rock."""
    width, height = 56, 36

    rock = [[True] * width for _ in range(height)]

    def carve(x, y, across, down):
        for j in range(y, y + down):
            for i in range(x, x + across):
                rock[j][i] = False

    carve(3, 14, 9, 8)        # where it starts, on the left
    carve(12, 17, 6, 2)       # the passage east
    carve(18, 11, 15, 13)     # the hall of pillars, in the middle
    carve(24, 6, 2, 5)        # north, to the gate
    carve(19, 1, 12, 5)       # the way out, at the top
    carve(33, 16, 5, 2)       # east again
    carve(38, 9, 15, 15)      # the lava, on the right
    carve(24, 24, 2, 4)       # south
    carve(12, 28, 28, 6)      # the cave, at the bottom

    ground = Grid(width, height)
    walls = Grid(width, height)
    molten = Grid(width, height)
    decor = Grid(width, height)
    glow = Grid(width, height)

    def near_floor(x, y):
        return any(not rock[j][i] for j in range(max(0, y - 2), min(height, y + 3)) for i in range(max(0, x - 2), min(width, x + 3)))

    for y in range(height):
        for x in range(width):
            if not rock[y][x]:
                pick = noise(x, y, 201)
                ground.put(x, y, CRACKED if pick > 0.94 else FLOOR3 if pick > 0.66 else FLOOR2 if pick > 0.33 else FLOOR)
            elif near_floor(x, y):
                # Only the rock next to somewhere to stand is drawn, the rest of it is the dark
                pick = noise(x, y, 203)
                walls.put(x, y, MOSSY if y >= 25 and pick > 0.5 else WALL2 if pick > 0.5 else WALL)

    # The pillars of the hall
    for x, y in [(21, 14), (28, 14), (21, 20), (28, 20)]:
        walls.put(x, y, PILLAR)

    # The lake of lava, with an island in it and a way across
    for y in range(12, 22):
        for x in range(41, 51):
            island = 44 <= x <= 47 and 15 <= y <= 18
            causeway = 41 <= x <= 43 and y in (16, 17)
            if causeway:
                ground.put(x, y, BRIDGE)
            elif not island:
                molten.put(x, y, LAVA)

    for x, y in [(23, 16), (26, 21), (30, 13), (15, 31), (29, 31), (6, 20)]:
        decor.put(x, y, PUDDLE)
    for x, y in [(39, 10), (52, 22), (13, 29), (36, 33)]:
        decor.put(x, y, BONES)

    # What glows. Signs in the floor where it starts and where it ends, crystal growing round each of the three
    # that are there to be found, and the cave's mushrooms
    for x, y, tile in [(6, 17, RUNE), (8, 17, RUNE2), (6, 19, RUNE2), (8, 19, RUNE),
                       (22, 2, RUNE), (27, 2, RUNE2), (22, 4, RUNE2), (27, 4, RUNE),
                       (31, 22, SHARDS_BLUE), (32, 23, SHARDS_BLUE), (32, 21, SHARDS_BLUE), (18, 23, SHARDS_BLUE),
                       (44, 15, SHARDS_RED), (47, 18, SHARDS_RED),
                       (38, 30, SHARDS_GREEN), (39, 32, SHARDS_GREEN), (39, 29, SHARDS_GREEN)]:
        glow.put(x, y, tile)

    for y in range(28, 34):
        for x in range(12, 37):
            if noise(x, y, 207) > 0.86:
                glow.put(x, y, MUSHROOM if noise(x, y, 209) > 0.5 else MUSHROOM2)

    for y in (2, 3):
        for x in (24, 25):
            glow.put(x, y, POOL)

    things = []

    def point(name, kind, x, y, properties=()):
        says = ""
        if properties:
            says = "   <properties>\n" + "".join(f'    <property name="{key}" type="{sort}" value="{value}"/>\n' for key, sort, value in properties) + "   </properties>\n"
        things.append(f'  <object id="{len(things) + 1}" name="{name}" type="{kind}" x="{x * T + 8}" y="{y * T + 8}">\n{says}   <point/>\n  </object>\n')

    def box(name, kind, x, y, across, down, properties=()):
        says = ""
        if properties:
            says = "   <properties>\n" + "".join(f'    <property name="{key}" type="{sort}" value="{value}"/>\n' for key, sort, value in properties) + "   </properties>\n"
        close = "/>\n" if not says else f">\n{says}  </object>\n"
        things.append(f'  <object id="{len(things) + 1}" name="{name}" type="{kind}" x="{x * T}" y="{y * T}" width="{across * T}" height="{down * T}"{close}')

    point("player", "spawn", 7, 18)

    point("blue", "crystal", 30, 21, [("colour", "color", "#ff5aaaff")])
    point("red", "crystal", 46, 16, [("colour", "color", "#ffff545c")])
    point("green", "crystal", 37, 31, [("colour", "color", "#ff6eff96")])

    # The one by the door is burning already, the rest are for whoever comes by with a spark
    point("first", "brazier", 4, 15, [("lit", "bool", "true")])
    for index, (x, y) in enumerate([(10, 20), (18, 11), (32, 11), (18, 23), (38, 9), (52, 23), (12, 28), (39, 28), (19, 5), (30, 5)]):
        point(f"brazier {index + 1}", "brazier", x, y)

    for x, y in [(27, 13), (22, 21), (39, 21), (51, 11), (18, 30), (26, 32), (33, 29)]:
        point("slime", "slime", x, y)

    for x, y in [(9, 16), (19, 16), (20, 19), (19, 19), (40, 19), (15, 29)]:
        point("crate", "crate", x, y)

    box("gate", "gate", 24, 8, 2, 1)
    box("way out", "exit", 24, 2, 2, 2)

    box("start", "zone", 3, 14, 9, 8, [("says", "string", "w a s d to walk, space or a click to throw a spark")])
    box("hall", "zone", 18, 11, 15, 13, [("says", "string", "a spark lights a brazier, and sends a slime on its way")])
    box("lava", "zone", 38, 9, 15, 15, [("says", "string", "nobody put a lamp in here, the lava lights the room by itself")])
    box("cave", "zone", 12, 28, 28, 6, [("says", "string", "nobody put a lamp down here, that is all mushrooms")])
    box("gate room", "zone", 24, 9, 2, 2, [("says", "string", "the gate wants all three crystals")])

    write("dungeon.tmx", f"""
<?xml version="1.0" encoding="UTF-8"?>
<map version="1.10" tiledversion="1.11.0" orientation="orthogonal" renderorder="right-down" width="{width}" height="{height}" tilewidth="{T}" tileheight="{T}" infinite="0" backgroundcolor="#030305" nextlayerid="7" nextobjectid="{len(things) + 1}">
 <properties>
  <property name="ambient" type="color" value="#ff161a26"/>
  <property name="GeometryOcclusion" type="bool" value="true"/>
 </properties>
 <tileset firstgid="1" source="tiles.tsx"/>
{layer(1, "floor", ground)}{layer(2, "decor", decor)}{layer(3, "lava", molten, [("Emissive", "float", "1"), ("IsCollidable", "bool", "true")])}{layer(4, "walls", walls, [("CastsShadows", "bool", "true"), ("IsCollidable", "bool", "true")])}{layer(5, "glow", glow, [("Emissive", "float", "1")])} <objectgroup id="6" name="things">
{"".join(things)} </objectgroup>
</map>
""")


def main():
    paint_tiles()
    paint_sprites()
    write_tileset()
    write_map()
    print(f"wrote the dungeon to {os.path.normpath(OUT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
