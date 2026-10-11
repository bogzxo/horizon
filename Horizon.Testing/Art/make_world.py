#!/usr/bin/env python3
"""Paints the tile set the examples share and writes the maps made out of it, into Assets/examples/world.

usage  python Art/make_world.py            from Horizon.Testing, whenever a tile or a map in here changes

The examples used to paint their art and write their maps as they started, into the temp folder, which meant nobody
could open a map in Tiled or look at a tile without running the thing. Now they are files like the files of a game,
and this is what makes them. What comes out, all 16 pixels a tile.

  tiles_albedo.png     what the tiles look like
  tiles_normal.png     which way their surface faces, worked out of how high every texel is
  tiles_specular.png   how shiny, the glazed bricks and the lantern glass and not much else
  tiles_ao.png         how hemmed in, out of the same heights
  tiles.tsx            the tile set, with the torch's two frames, the half step's collision and the sign's properties
  clouds.png           an image layer that repeats
  town.tmx             a street at night with one of everything a map can have, the tile map example and the big one
  cellar.tmx           a brick room with pillars and lamps, the lighting examples
  objects/*.tx         the templates the lamps of both are made from

Every tile is painted by a function that says, for a texel, what colour it is, how high it stands (0 at the back
of the wall, 1 at the front of a brick) and how shiny it is. The normal and the occlusion come out of the heights,
so they can never disagree with the picture.
"""
import math
import os
import sys

import numpy as np
from PIL import Image

T = 16          # texels a tile
COLUMNS = 8     # tiles a row of the sheet
ROWS = 4

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "Assets", "examples", "world")


def noise(x, y, seed=0):
    """A number from 0 to 1 that looks random and is the same for the same texel every time."""
    h = (x * 374761393 + y * 668265263 + seed * 2147483647) & 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0


def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def shade(colour, by):
    return tuple(max(0.0, min(255.0, c * by)) for c in colour)


# Every painter returns (colour, alpha, height, shine) for a texel, or None for nothing there


def brick(x, y, variant=0, glazed=False):
    # Two courses a tile, every other one shifted by half a brick, the same wall wall.slang used to make up
    course = y // 8
    bx = (x + (8 if course % 2 else 0)) % 16
    by = y % 8
    edge = min(bx, 15 - bx, by, 7 - by)
    which = (course + variant * 3 + ((x + (8 if course % 2 else 0)) // 16)) % 5

    if edge < 1:
        return (52, 49, 54), 255, 0.0, 0.0

    tint = noise(which, course, variant)
    base = mix((108, 77, 70), (148, 112, 94), tint)
    shine = 0.0
    if glazed and course % 2 == 0:
        base = (58, 88, 112)
        shine = 0.9

    # A brick slopes away towards its edges, which is what catches the light
    height = min(1.0, 0.35 + edge * 0.3)
    speck = 0.92 + 0.16 * noise(x, y, 7 + variant)
    return shade(base, speck), 255, height, shine


def planks(x, y):
    board = x // 4
    edge = min(x % 4, 3 - x % 4)
    base = mix((92, 66, 48), (116, 84, 58), noise(board, 0, 3))
    if edge == 0 and x % 4 == 0:
        return shade(base, 0.6), 255, 0.2, 0.0
    knot = 0.85 if noise(x, y // 3, 11) > 0.93 else 1.0
    return shade(base, knot), 255, 0.8, 0.0


def window(x, y):
    frame = x in (0, 15) or y in (0, 15) or x in (7, 8) or y in (7, 8)
    if frame:
        return (70, 52, 40), 255, 1.0, 0.0
    glow = 0.8 + 0.2 * noise(x // 3, y // 3, 5)
    return shade((255, 214, 130), glow), 255, 0.3, 0.6


def door(x, y, top):
    if x in (0, 1, 14, 15) or (top and y in (0, 1)):
        return (64, 46, 36), 255, 1.0, 0.0
    panel = min(abs(x - 7.5), 5.0) > 4.0 or (y % 8 in (0, 7))
    base = (96, 64, 44) if not panel else (78, 52, 36)
    if not top and x in (11, 12) and y in (2, 3):
        return (224, 190, 96), 255, 1.0, 0.8
    return base, 255, 0.7 if not panel else 0.45, 0.0


def ground_top(x, y):
    if y < 3:
        lip = (150, 158, 186) if noise(x // 2, 0, 2) > 0.3 else (128, 136, 164)
        return lip, 255, 1.0 - y * 0.1, 0.15
    return ground_body(x, y)


def ground_body(x, y, variant=0):
    # Big stones, their joints a texel deep
    course = y // 8
    bx = (x + (5 if course % 2 else 0) + variant * 3) % 16
    joint = bx % 8 == 0 or y % 8 == 0
    if joint:
        return (38, 40, 56), 255, 0.1, 0.0
    base = mix((70, 74, 98), (88, 92, 120), noise(bx // 8, course, variant + 1))
    return shade(base, 0.93 + 0.14 * noise(x, y, 9)), 255, 0.8, 0.0


def beam(x, y):
    if y >= 6:
        return None
    base = (124, 92, 60) if y not in (0, 5) else (86, 62, 42)
    if x % 8 == 0:
        base = (70, 50, 36)
    return base, 255, 1.0 - abs(y - 2.5) * 0.15, 0.0


def crate(x, y):
    rim = min(x, 15 - x, y, 15 - y)
    if rim < 2:
        return (72, 48, 32), 255, 1.0, 0.0
    cross = abs(x - y) < 2 or abs(x + y - 15) < 2
    base = (112, 78, 48) if not cross else (86, 58, 36)
    return shade(base, 0.94 + 0.12 * noise(x // 2, y, 4)), 255, 0.9 if cross else 0.6, 0.0


def barrel(x, y):
    half = 6.5 - 1.5 * abs(y - 7.5) / 7.5 * abs(y - 7.5) / 7.5
    off = abs(x - 7.5)
    if off > half or y < 1:
        return None
    hoop = y in (3, 4, 11, 12)
    base = (86, 90, 100) if hoop else mix((120, 72, 46), (92, 54, 36), off / half)
    # Round, so it stands proud in the middle and falls away to the sides
    return base, 255, math.sqrt(max(0.0, 1.0 - (off / half) ** 2)), 0.5 if hoop else 0.0


def pillar(x, y, top):
    if top and y < 4:
        if x < 1 or x > 14:
            return None
        return (150, 150, 160), 255, 1.0, 0.1
    if x < 3 or x > 12:
        return None
    flute = (x - 3) % 3 == 0
    base = (120, 120, 132) if not flute else (92, 92, 104)
    return base, 255, 0.5 if flute else 0.9, 0.05


def torch(x, y, frame):
    if x in (7, 8) and y >= 8:
        return (96, 68, 44), 255, 0.8, 0.0
    cx, cy = 7.5 + (0.5 if frame else -0.5), 5.0 - (1.0 if frame else 0.0)
    d = math.hypot((x - cx) * 1.3, y - cy)
    reach = 4.2 if frame else 3.6
    if d < reach * 0.45:
        return (255, 246, 190), 255, 1.0, 0.0
    if d < reach:
        return (255, 150, 50), 255, 0.8, 0.0
    return None


def lantern(x, y):
    if x in (7, 8) and y < 4:
        return (60, 56, 60), 255, 0.6, 0.0
    if 4 <= y <= 12 and 4 <= x <= 11:
        if y in (4, 12) or x in (4, 11):
            return (150, 40, 36), 255, 1.0, 0.2
        return (255, 196, 96), 255, 0.6, 0.7
    return None


def sign(x, y):
    if x in (7, 8) and y >= 10:
        return (96, 68, 44), 255, 0.6, 0.0
    if 2 <= y <= 9 and 1 <= x <= 14:
        if y in (2, 9) or x in (1, 14):
            return (110, 76, 46), 255, 1.0, 0.0
        writing = y in (4, 6) and 3 <= x <= 12 and (x + y) % 3 != 0
        return ((70, 50, 36) if writing else (206, 180, 128)), 255, 0.7, 0.0
    return None


def arrow(x, y):
    # Points up and has a dot in one corner, so every way of turning it over looks different from every other
    if x in (0, 15) or y in (0, 15):
        return (40, 44, 72), 255, 1.0, 0.0
    if 2 <= x <= 4 and 2 <= y <= 4:
        return (40, 120, 220), 255, 0.9, 0.0
    shaft = x in (7, 8) and 3 <= y <= 12
    head = 3 <= y <= 7 and abs(x - 7.5) <= y - 2.5
    if shaft or head:
        return (214, 60, 70), 255, 0.9, 0.0
    return (244, 236, 214), 255, 0.5, 0.0


def step(x, y):
    if y < 8:
        return None
    lip = y in (8, 9)
    return ((170, 174, 196) if lip else (96, 100, 126)), 255, 1.0 if lip else 0.8, 0.0


def grass(x, y):
    blade = (x * 7 + 3) % 5
    tall = 6 + blade * 2 if x % 2 == 0 else 3 + blade
    if 15 - y < tall and x % 3 != 1:
        return ((70, 150, 72) if (x + y) % 2 else (96, 182, 88)), 255, 0.6, 0.0
    return None


def pole(x, y):
    if x in (7, 8):
        return ((112, 60, 56) if x == 7 else (86, 44, 44)), 255, 0.9, 0.2
    return None


def hill(x, y, top):
    if top:
        ridge = 6 + 3.0 * math.sin(x / 16.0 * math.tau) + 1.5 * math.sin(x / 16.0 * math.tau * 2 + 1)
        if y < ridge:
            return None
    return (54, 70, 104), 255, 0.5, 0.0


def roof(x, y, part):
    # part -1 the left end, 0 the middle, 1 the right end
    slope = x if part < 0 else 15 - x if part > 0 else 99
    if y < 10 - min(slope, 10):
        return None
    tile = (y // 4 + (x + (2 if (y // 4) % 2 else 0)) // 4) % 2
    base = (76, 92, 128) if tile else (60, 74, 108)
    lip = y % 4 == 0
    return shade(base, 0.75 if lip else 1.0), 255, 0.4 if lip else 0.9, 0.25


def vines(x, y):
    strand = (x * 5 + 2) % 7
    length = 5 + strand * 2
    if y < length and x % 3 == 0:
        return ((48, 110, 60) if y % 2 else (62, 132, 70)), 255, 0.8, 0.0
    if y < length and x % 3 == 1 and y % 4 == 2:
        return (84, 160, 84), 255, 0.9, 0.0
    return None


TILES = [
    lambda x, y: brick(x, y, 0), lambda x, y: brick(x, y, 1), lambda x, y: brick(x, y, 2, glazed=True), planks,
    window, lambda x, y: door(x, y, True), lambda x, y: door(x, y, False), lambda x, y: ground_body(x, y, 2),

    ground_top, ground_body, lambda x, y: ground_body(x, y, 1), beam,
    crate, barrel, lambda x, y: pillar(x, y, True), lambda x, y: pillar(x, y, False),

    lambda x, y: torch(x, y, 0), lambda x, y: torch(x, y, 1), lantern, sign,
    arrow, step, grass, pole,

    lambda x, y: hill(x, y, True), lambda x, y: hill(x, y, False), lambda x, y: roof(x, y, -1), lambda x, y: roof(x, y, 0),
    lambda x, y: roof(x, y, 1), vines, None, None,
]

# The numbers a map knows the tiles by, one more than where they are in the sheet (0 is nothing)
BRICK, BRICK2, GLAZED, PLANKS, WINDOW, DOOR_TOP, DOOR, STONE3 = range(1, 9)
GROUND, STONE, STONE2, BEAM, CRATE, BARREL, PILLAR_TOP, PILLAR = range(9, 17)
TORCH, TORCH2, LANTERN, SIGN, ARROW, STEP, GRASS, POLE = range(17, 25)
HILL_TOP, HILL, ROOF_L, ROOF, ROOF_R, VINES = range(25, 31)

# Which tiles tile with themselves, so their heights wrap at the edge when the slopes are worked out
WRAPPING = {BRICK, BRICK2, GLAZED, PLANKS, STONE3, STONE, STONE2}


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


def paint_clouds():
    image = Image.new("RGBA", (128, 32))
    pixels = image.load()
    for cx, cy, radius in [(14, 18, 9), (26, 14, 12), (40, 18, 10), (52, 20, 7), (84, 16, 8), (96, 12, 11), (110, 17, 9)]:
        for y in range(24):
            for x in range(128):
                if (x - cx) ** 2 + (y - cy) ** 2 * 2 < radius * radius:
                    pixels[x, y] = (196, 206, 236, 200) if y > cy + 2 else (226, 232, 250, 215)
    image.save(os.path.join(OUT, "clouds.png"))


def write(name, text):
    path = os.path.join(OUT, name)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as file:
        file.write(text.lstrip("\n"))


def write_tileset():
    write("tiles.tsx", f"""
<?xml version="1.0" encoding="UTF-8"?>
<tileset version="1.10" tiledversion="1.11.0" name="tiles" tilewidth="{T}" tileheight="{T}" tilecount="{COLUMNS * ROWS}" columns="{COLUMNS}">
 <image source="tiles_albedo.png" width="{COLUMNS * T}" height="{ROWS * T}"/>
 <tile id="{TORCH - 1}">
  <animation>
   <frame tileid="{TORCH - 1}" duration="140"/>
   <frame tileid="{TORCH2 - 1}" duration="180"/>
  </animation>
 </tile>
 <tile id="{STEP - 1}">
  <objectgroup draworder="index" id="2">
   <object id="1" x="0" y="8" width="16" height="8"/>
  </objectgroup>
 </tile>
 <tile id="{SIGN - 1}" type="sign">
  <properties>
   <property name="label" value="mind the step"/>
  </properties>
 </tile>
</tileset>
""")


def write_templates():
    # What a lamp is, said once. A map puts one down and only says what is different about it
    write("objects/lantern.tx", """
<?xml version="1.0" encoding="UTF-8"?>
<template>
 <object type="light">
  <properties>
   <property name="light_colour" type="color" value="#ffffc880"/>
   <property name="light_flicker" type="float" value="0.12"/>
   <property name="light_intensity" type="float" value="1.3"/>
   <property name="light_radius" type="float" value="150"/>
  </properties>
  <point/>
 </object>
</template>
""")
    write("objects/spot.tx", """
<?xml version="1.0" encoding="UTF-8"?>
<template>
 <object type="light">
  <properties>
   <property name="light_colour" type="color" value="#ffffe0b0"/>
   <property name="light_cone" type="float" value="70"/>
   <property name="light_intensity" type="float" value="1.6"/>
   <property name="light_radius" type="float" value="260"/>
   <property name="light_type" value="spot"/>
  </properties>
  <point/>
 </object>
</template>
""")
    write("objects/spawn.tx", """
<?xml version="1.0" encoding="UTF-8"?>
<template>
 <object type="spawn">
  <point/>
 </object>
</template>
""")


class Grid:
    """A layer as rows of numbers, filled in a tile at a time and written out the way Tiled writes them."""

    def __init__(self, width, height):
        self.width, self.height = width, height
        self.cells = [[0] * width for _ in range(height)]

    def put(self, x, y, gid):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.cells[y][x] = gid

    def fill(self, x, y, across, down, gid):
        for j in range(down):
            for i in range(across):
                self.put(x + i, y + j, gid(x + i, y + j) if callable(gid) else gid)

    def csv(self):
        return ",\n".join(",".join(str(cell) for cell in row) for row in self.cells)


def layer(identity, name, grid, attributes="", properties=None):
    inner = ""
    if properties:
        inner = "  <properties>\n" + "".join(
            f'   <property name="{key}" type="{kind}" value="{value}"/>\n' for key, kind, value in properties) + "  </properties>\n"
    return (f' <layer id="{identity}" name="{name}" width="{grid.width}" height="{grid.height}"{attributes}>\n{inner}'
            f'  <data encoding="csv">\n{grid.csv()}\n</data>\n </layer>\n')


H, V, D = 0x80000000, 0x40000000, 0x20000000


def write_town():
    """A street at night, 64 tiles by 24. The floor is rows 18 and down, a raised bit on the left, houses behind."""
    width, height, floor = 64, 24, 18

    hills = Grid(width, height)
    for x in range(width):
        hills.put(x, 12, HILL_TOP)
        hills.fill(x, 13, 1, 5, HILL)

    houses = Grid(width, height)
    glow = Grid(width, height)

    def house(left, across, up, shiny=False):
        top = floor - up
        houses.fill(left, top, across, up, lambda x, y: GLAZED if shiny and (x + y) % 5 == 0 else BRICK if (x * 3 + y) % 4 else BRICK2)
        houses.put(left, top - 1, ROOF_L)
        houses.fill(left + 1, top - 1, across - 2, 1, ROOF)
        houses.put(left + across - 1, top - 1, ROOF_R)
        for x in range(left + 1, left + across - 1, 3):
            # Lit from inside, so they go on the layer that shows no matter the light
            glow.put(x, top + 1, WINDOW)
            if up > 5:
                glow.put(x, top + 4, WINDOW)
        houses.put(left + across // 2, floor - 2, DOOR_TOP)
        houses.put(left + across // 2, floor - 1, DOOR)

    house(2, 9, 6)
    house(13, 8, 8, shiny=True)
    house(24, 11, 5)
    house(38, 8, 9, shiny=True)
    house(48, 12, 6)
    houses.fill(21, floor - 3, 3, 3, PLANKS)
    houses.fill(35, floor - 3, 3, 3, PLANKS)

    # What is stood on and what blocks light, the street, a raised kerb, a few things put down on it
    solid = Grid(width, height)
    solid.fill(0, floor, width, 1, GROUND)
    solid.fill(0, floor + 1, width, height - floor - 1, lambda x, y: STONE if (x // 2 + y) % 3 else STONE2)
    solid.fill(0, floor - 2, 10, 1, GROUND)
    solid.fill(0, floor - 1, 10, 1, STONE)
    solid.put(10, floor - 1, STEP)
    for x, stack in [(18, 1), (19, 2), (20, 1), (33, 1), (45, 2), (46, 1), (58, 1)]:
        for level in range(stack):
            solid.put(x, floor - 1 - level, CRATE)
    for x in (28, 52):
        solid.put(x, floor - 1, BARREL)
    for x in (30, 42):
        solid.put(x, floor - 4, PILLAR_TOP)
        solid.fill(x, floor - 3, 1, 3, PILLAR)
    solid.fill(22, floor - 5, 5, 1, BEAM)

    decor = Grid(width, height)
    for x in (4, 5, 12, 13, 14, 25, 26, 36, 37, 49, 50, 55, 60):
        decor.put(x, floor - 1 if x > 10 else floor - 3, GRASS)
    decor.put(15, floor - 1, SIGN)
    for x in (11, 27, 43, 56):
        decor.fill(x, floor - 5, 1, 5, POLE)
    # The same tile turned every way a tile can be, in a row up on the beam
    for index, turn in enumerate([0, H, V, H | V, D, D | H, D | V, D | H | V]):
        decor.put(47 + index, floor - 9, ARROW | turn)

    for x in (11, 27, 43, 56):
        glow.put(x, floor - 6, LANTERN)
    for x in (7, 34, 53):
        glow.put(x, floor - 3 if x > 10 else floor - 5, TORCH)

    front = Grid(width, height)
    for x in range(21, 28):
        front.put(x, floor - 4, VINES)
    for x in list(range(0, 4)) + list(range(60, 64)):
        front.put(x, 0, VINES)
        front.put(x, 1, VINES)

    def lamp(identity, name, x, y, extra=""):
        return f'  <object id="{identity}" name="{name}" template="objects/lantern.tx" x="{x * T + 8}" y="{y * T + 8}">{extra}</object>\n'

    objects = ""
    for index, x in enumerate((11, 27, 43, 56)):
        objects += lamp(index + 1, f"lantern {index + 1}", x, floor - 6)
    for index, x in enumerate((7, 34, 53)):
        objects += lamp(index + 5, f"torch {index + 1}", x, floor - 3 if x > 10 else floor - 5, """
   <properties>
    <property name="light_colour" type="color" value="#ffff9040"/>
    <property name="light_flicker" type="float" value="0.3"/>
    <property name="light_radius" type="float" value="110"/>
   </properties>
  """)
    objects += f'  <object id="8" name="moon" type="light" x="{40 * T}" y="{2 * T}">\n   <properties>\n'
    objects += '    <property name="light_colour" type="color" value="#ff8ca6e6"/>\n    <property name="light_direction" type="float" value="-70"/>\n'
    objects += '    <property name="light_intensity" type="float" value="0.3"/>\n    <property name="light_type" value="directional"/>\n   </properties>\n   <point/>\n  </object>\n'
    objects += f'  <object id="9" name="player" template="objects/spawn.tx" x="{16 * T}" y="{floor * T}"/>\n'
    objects += f'  <object id="10" name="porch" type="zone" x="{22 * T}" y="{(floor - 4) * T}" width="{5 * T}" height="{4 * T}"/>\n'
    objects += f'  <object id="11" name="notice" gid="{SIGN}" x="{61 * T}" y="{floor * T}" width="16" height="16"/>\n'

    write("town.tmx", f"""
<?xml version="1.0" encoding="UTF-8"?>
<map version="1.10" tiledversion="1.11.0" orientation="orthogonal" renderorder="right-down" width="{width}" height="{height}" tilewidth="{T}" tileheight="{T}" infinite="0" backgroundcolor="#1c2440" nextlayerid="12" nextobjectid="12">
 <properties>
  <property name="ambient" type="color" value="#ff30364e"/>
  <property name="GeometryOcclusion" type="bool" value="true"/>
 </properties>
 <tileset firstgid="1" source="tiles.tsx"/>
 <imagelayer id="1" name="clouds" offsety="24" parallaxx="0.15" repeatx="1">
  <image source="clouds.png" width="128" height="32"/>
  <properties>
   <property name="Emissive" type="float" value="0.6"/>
   <property name="ScrollX" type="float" value="6"/>
  </properties>
 </imagelayer>
 <group id="2" name="backdrop" parallaxx="0.5">
{layer(3, "hills", hills, ' opacity="0.8"', [("Emissive", "float", "0.35")])} </group>
{layer(4, "houses", houses)}{layer(5, "solid", solid, "", [("CastsShadows", "bool", "true"), ("IsCollidable", "bool", "true")])}{layer(6, "decor", decor)}{layer(7, "glow", glow, "", [("Emissive", "float", "1")])}{layer(8, "front", front, ' opacity="0.9"', [("Foreground", "bool", "true")])} <objectgroup id="9" name="things">
{objects} </objectgroup>
</map>
""")


def write_cellar():
    """A brick room, 50 by 30, with pillars and ledges for the lights to be blocked by. The lighting examples."""
    width, height = 50, 30

    wall = Grid(width, height)
    wall.fill(0, 0, width, height, lambda x, y: GLAZED if (x * 7 + y * 3) % 11 == 0 else BRICK if (x + y * 2) % 3 else BRICK2)

    solid = Grid(width, height)
    solid.fill(0, height - 2, width, 2, lambda x, y: GROUND if y == height - 2 else STONE)
    solid.fill(0, 0, width, 1, STONE)
    for x, top, tall in [(9, 16, 12), (38, 20, 8)]:
        solid.put(x, top, PILLAR_TOP)
        solid.fill(x, top + 1, 1, tall - 1, PILLAR)
    solid.fill(15, 12, 8, 1, lambda x, y: STONE2 if x % 2 else STONE)
    solid.fill(27, 19, 6, 1, lambda x, y: STONE2 if x % 2 else STONE)
    solid.fill(22, 23, 3, 2, lambda x, y: STONE3)
    solid.fill(42, 10, 5, 2, lambda x, y: STONE3)
    for x, y in [(4, 27), (5, 27), (5, 26), (31, 27), (45, 27)]:
        solid.put(x, y, CRATE)
    solid.put(17, 27, BARREL)

    glow = Grid(width, height)
    for x, y in [(12, 8), (25, 6), (36, 9)]:
        glow.put(x, y, LANTERN)

    objects = ""
    for index, (x, y) in enumerate([(12, 8), (25, 6), (36, 9)]):
        colour = ["#ffff5a40", "#ff5aff66", "#ff5a80ff"][index]
        objects += (f'  <object id="{index + 1}" name="lantern {index + 1}" template="objects/lantern.tx" x="{x * T + 8}" y="{y * T + 8}">\n'
                    f'   <properties>\n    <property name="light_colour" type="color" value="{colour}"/>\n   </properties>\n  </object>\n')
    objects += f'  <object id="4" name="hanging" template="objects/spot.tx" x="{25 * T}" y="{1 * T + 4}"/>\n'
    objects += f'  <object id="5" name="player" template="objects/spawn.tx" x="{20 * T}" y="{(height - 2) * T}"/>\n'

    write("cellar.tmx", f"""
<?xml version="1.0" encoding="UTF-8"?>
<map version="1.10" tiledversion="1.11.0" orientation="orthogonal" renderorder="right-down" width="{width}" height="{height}" tilewidth="{T}" tileheight="{T}" infinite="0" backgroundcolor="#05050a" nextlayerid="5" nextobjectid="6">
 <properties>
  <property name="ambient" type="color" value="#ff2e3348"/>
 </properties>
 <tileset firstgid="1" source="tiles.tsx"/>
{layer(1, "wall", wall)}{layer(2, "solid", solid, "", [("CastsShadows", "bool", "true"), ("IsCollidable", "bool", "true")])}{layer(3, "glow", glow, "", [("Emissive", "float", "1")])} <objectgroup id="4" name="things">
{objects} </objectgroup>
</map>
""")


def main():
    paint_tiles()
    paint_clouds()
    write_tileset()
    write_templates()
    write_town()
    write_cellar()
    print(f"wrote the world to {os.path.normpath(OUT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
