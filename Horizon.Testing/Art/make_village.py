#!/usr/bin/env python3
"""Puts the village of the village example together, into Assets/examples/village.

usage  python Art/make_village.py                       from Horizon.Testing, rewrites the maps out of the sheets that are there
       python Art/make_village.py <tiny-rpg-town-files> <axulart-interior-folder>
                                                        and the sheets as well, out of the two packs as they come off itch.io

Nothing in here is painted. The art is two packs off itch.io (see the README next to the maps for who made what
and under which licence), this only cuts what the example wants out of them, puts the loose grass tiles of the
town sheet onto the end of it, and writes the maps, which open in Tiled like any other.

  town.png       the town sheet of Tiny RPG Town, 22 tiles across, with its credit box cleared and the three
                 grass tiles on three rows under it (it is one picture of a village and the map is cut out of it,
                 a house is a rectangle of its tiles stamped down somewhere)
  interior.png   AxulArt's house interior, walls, floors, a bed, a telly, plants
  lab.png        the second tech lab of Tiny RPG Town, which is what a professor's lab looks like round here
  cave.png       the cave of the same
  hero.png       the hero, 3 frames a row, a row a way he faces (down, up, right, left)
  npc.png        four villagers, a row each, facing down, sideways and up
  *_normal.png   which way every texel of the four tile sets faces, how shiny it is and how hemmed in, all three
  *_specular.png worked out of the pictures (see make_maps), the packs came with none
  *_ao.png

  village.tmx    the town, with doors on the buildings that lead into
  house_a.tmx    a house
  house_b.tmx    another house
  lab.tmx        the lab, which is bigger on the inside
  cave.tmx       the cave in the rocks at the top

A door is an object of class "door" with a file property "door" naming the map behind it, see TileMapRooms in
the engine for the whole of what it goes by. The lamps are objects of class "light" with the light_* properties
the other examples use (World.AddLights), the ones that only come on at night say so with "night".
"""
import os
import shutil
import sys

import numpy as np
from PIL import Image

T = 16

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "Assets", "examples", "village")

# The town sheet is 22 tiles across and 18 down, and three rows of grass go under that
TOWN_COLUMNS, TOWN_ROWS = 22, 21
INTERIOR_COLUMNS, INTERIOR_ROWS = 12, 10
LAB_COLUMNS, LAB_ROWS = 20, 13
CAVE_COLUMNS, CAVE_ROWS = 13, 10

H, V, D = 0x80000000, 0x40000000, 0x20000000


# The sheets


def write(name, text):
    path = os.path.join(OUT, name)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as file:
        file.write(text.lstrip("\n"))


def make_sheets(town_pack, interior_pack):
    """Copies the pictures the example draws with out of the two packs, as they are but for the town sheet."""
    environments = os.path.join(town_pack, "Assets", "Environments")

    # The town sheet with its credit box taken out (the bottom right corner, over the path cross) and the three
    # loose grass tiles put on under it, so one tile set covers the whole town
    town = Image.open(os.path.join(environments, "Town", "tileset", "tileset.png")).convert("RGBA")
    sheet = Image.new("RGBA", (TOWN_COLUMNS * T, TOWN_ROWS * T), (0, 0, 0, 0))
    sheet.paste(town, (0, 0))
    for x in range(14 * T, 22 * T):
        for y in range(15 * T, 18 * T):
            sheet.putpixel((x, y), (0, 0, 0, 0))
    for index, name in enumerate(["grass-tile.png", "grass-tile-2.png", "grass-tile-3.png"]):
        grass = Image.open(os.path.join(environments, "Town", "tileset", name)).convert("RGBA")
        sheet.paste(grass, (index * 3 * T, 18 * T))
    sheet.save(os.path.join(OUT, "town.png"))

    shutil.copyfile(os.path.join(environments, "Techlab 2", "tileset.png"), os.path.join(OUT, "lab.png"))
    shutil.copyfile(os.path.join(environments, "Cave", "tileset.png"), os.path.join(OUT, "cave.png"))
    shutil.copyfile(os.path.join(environments, "Town", "spritesheets", "hero.png"), os.path.join(OUT, "hero.png"))
    shutil.copyfile(os.path.join(environments, "Town", "spritesheets", "npc.png"), os.path.join(OUT, "npc.png"))

    # The interior comes as a palette picture, the engine wants it as colours with an alpha
    for name in os.listdir(interior_pack):
        if name.lower().endswith(".png") and "_ALL_" not in name:
            Image.open(os.path.join(interior_pack, name)).convert("RGBA").save(os.path.join(OUT, "interior.png"))


def make_maps():
    """The normal, specular and occlusion maps of the four tile sets, worked out of the pictures themselves. The
    engine picks them up by name (town_normal.png next to town.png and so on, see TileMapTexture), nothing has to
    be said for them to be found.

    Nobody painted a height for this art, so the height is taken to be how light a texel is, which for pixel art
    shaded the usual way (the lit side light, the far side dark) is near enough that the low sun rakes across a
    roof the right way. A tile is worked out on its own, its neighbours on the sheet are not next to it on the
    map. Shine is a base for the sheet and more wherever the paint is bluer than it is anything else, which on
    these sheets is water, glass and screens. Occlusion is a texel being darker than the opaque texels round it
    over a few sizes of neighbourhood, which is a texel in a crack or under an edge."""
    sheets = [("town", 0.08, 0.5), ("interior", 0.14, 0.4), ("lab", 0.3, 0.45), ("cave", 0.05, 0.6)]
    for name, base_shine, depth in sheets:
        path = os.path.join(OUT, f"{name}.png")
        if not os.path.exists(path):
            continue

        picture = np.asarray(Image.open(path).convert("RGBA")).astype(np.float64)
        height, width = picture.shape[0], picture.shape[1]
        rgb, alpha = picture[..., :3], picture[..., 3]
        opaque = alpha > 0
        lum = (rgb[..., 0] * 0.299 + rgb[..., 1] * 0.587 + rgb[..., 2] * 0.114) / 255.0

        normal = np.zeros((height, width, 4), dtype=np.float64)
        normal[..., 0:2] = 128.0
        normal[..., 2] = 255.0
        occlusion = np.full((height, width), 255.0)

        for top in range(0, height, T):
            for left in range(0, width, T):
                mask = opaque[top:top + T, left:left + T]
                if not mask.any():
                    continue
                tile = lum[top:top + T, left:left + T]

                # The height, the lightness smoothed a little over the texels that are there, so one dithered
                # texel isn't a spike. Blurring over the see-through texels would pull every edge towards nothing
                blurred = masked_blur(tile, mask, 1)

                # The slopes, with the edge of the tile held flat. Downhill is where the surface faces, the
                # picture counts its rows down and a normal map has its Y up. Gently, the lightness of pixel art
                # is mostly paint and not relief, at 2.2 every roof tile was a cobble under the low sun
                padded = np.pad(blurred, 1, mode="edge")
                dx = (padded[1:-1, 2:] - padded[1:-1, :-2]) * 0.5
                dy = (padded[2:, 1:-1] - padded[:-2, 1:-1]) * 0.5
                strength = 0.55
                nx, ny, nz = -dx * strength, dy * strength, np.ones_like(dx)
                length = np.sqrt(nx * nx + ny * ny + nz * nz)
                block = normal[top:top + T, left:left + T]
                block[..., 0] = np.where(mask, nx / length * 127.5 + 127.5, 128.0)
                block[..., 1] = np.where(mask, ny / length * 127.5 + 127.5, 128.0)
                block[..., 2] = np.where(mask, nz / length * 127.5 + 127.5, 255.0)
                block[..., 3] = np.where(mask, 255.0, 0.0)

                # The occlusion, how much darker a texel is than its surroundings, the biggest of a few reaches
                darkest = np.zeros_like(tile)
                for reach in (1, 2, 4):
                    around = masked_blur(tile, mask, reach)
                    darkest = np.maximum(darkest, np.clip((around - tile) / 0.22, 0.0, 1.0))
                occlusion[top:top + T, left:left + T] = np.where(mask, (1.0 - depth * darkest) * 255.0, 255.0)

        # Shine, the base of the sheet and what is blue (water, glass, a screen) on top
        bluer = rgb[..., 2] - np.maximum(rgb[..., 0], rgb[..., 1])
        glass = np.clip((bluer - 8.0) / 40.0, 0.0, 1.0)
        shine = np.where(opaque, (base_shine + glass * 0.65) * 255.0, 0.0)

        Image.fromarray(normal.astype(np.uint8), "RGBA").save(os.path.join(OUT, f"{name}_normal.png"))
        grey = np.clip(shine, 0, 255).astype(np.uint8)
        Image.fromarray(np.dstack([grey, grey, grey, np.full_like(grey, 255)]), "RGBA").save(os.path.join(OUT, f"{name}_specular.png"))
        grey = np.clip(occlusion, 0, 255).astype(np.uint8)
        Image.fromarray(np.dstack([grey, grey, grey, np.full_like(grey, 255)]), "RGBA").save(os.path.join(OUT, f"{name}_ao.png"))


def masked_blur(values, mask, reach):
    """A box blur over the texels that are there and no others, a see-through texel counts for nothing."""
    weights = mask.astype(np.float64)
    summed = np.zeros_like(values)
    counted = np.zeros_like(values)
    padded_values = np.pad(values * weights, reach, mode="constant")
    padded_weights = np.pad(weights, reach, mode="constant")
    size = reach * 2 + 1
    for dy in range(size):
        for dx in range(size):
            summed += padded_values[dy:dy + values.shape[0], dx:dx + values.shape[1]]
            counted += padded_weights[dy:dy + values.shape[0], dx:dx + values.shape[1]]
    return np.where(counted > 0, summed / np.maximum(counted, 1e-9), values)


def write_tilesets():
    def tileset(name, image, columns, rows):
        write(f"{name}.tsx", f"""
<?xml version="1.0" encoding="UTF-8"?>
<tileset version="1.10" tiledversion="1.11.0" name="{name}" tilewidth="{T}" tileheight="{T}" tilecount="{columns * rows}" columns="{columns}">
 <image source="{image}" width="{columns * T}" height="{rows * T}"/>
</tileset>
""")

    tileset("town", "town.png", TOWN_COLUMNS, TOWN_ROWS)
    tileset("interior", "interior.png", INTERIOR_COLUMNS, INTERIOR_ROWS)
    tileset("lab", "lab.png", LAB_COLUMNS, LAB_ROWS)
    tileset("cave", "cave.png", CAVE_COLUMNS, CAVE_ROWS)


# Maps


class Grid:
    """A layer as rows of numbers, filled in a tile at a time and written out the way Tiled writes them."""

    def __init__(self, width, height):
        self.width, self.height = width, height
        self.cells = [[0] * width for _ in range(height)]

    def get(self, x, y):
        return self.cells[y][x] if 0 <= x < self.width and 0 <= y < self.height else 0

    def put(self, x, y, gid):
        if 0 <= x < self.width and 0 <= y < self.height:
            self.cells[y][x] = gid

    def fill(self, x, y, across, down, gid):
        for j in range(down):
            for i in range(across):
                self.put(x + i, y + j, gid(x + i, y + j) if callable(gid) else gid)

    def stamp(self, x, y, sheet, left, top, across, down, skip=()):
        """Copies a rectangle of a sheet down, cell for cell, leaving out the cells that are empty on the sheet."""
        for j in range(down):
            for i in range(across):
                if (left + i, top + j) in skip:
                    continue
                self.put(x + i, y + j, sheet(left + i, top + j))

    def any(self, x, y):
        return self.get(x, y) != 0

    def csv(self):
        return ",\n".join(",".join(str(cell) for cell in row) for row in self.cells)


def layer(identity, name, grid, attributes="", properties=None):
    inner = ""
    if properties:
        inner = "  <properties>\n" + "".join(
            f'   <property name="{key}" type="{kind}" value="{value}"/>\n' for key, kind, value in properties) + "  </properties>\n"
    return (f' <layer id="{identity}" name="{name}" width="{grid.width}" height="{grid.height}"{attributes}>\n{inner}'
            f'  <data encoding="csv">\n{grid.csv()}\n</data>\n </layer>\n')


def prop(name, value, kind=None):
    kind = f' type="{kind}"' if kind else ""
    return f'    <property name="{name}"{kind} value="{value}"/>\n'


def properties(*props):
    return ("   <properties>\n" + "".join(props) + "   </properties>\n") if props else ""


def point(identity, name, kind, x, y, *props):
    """An object at the middle of a cell."""
    return (f'  <object id="{identity}" name="{name}" type="{kind}" x="{x * T + T // 2}" y="{y * T + T // 2}">\n'
            f'{properties(*props)}   <point/>\n  </object>\n')


def box(identity, name, kind, x, y, across, down, *props):
    """An object over a rectangle of cells."""
    return (f'  <object id="{identity}" name="{name}" type="{kind}" x="{x * T}" y="{y * T}" width="{across * T}" height="{down * T}">\n'
            f'{properties(*props)}  </object>\n')


def door(identity, name, x, y, across, down, into, arrive, direction):
    """A door, see TileMapRooms. Where it is, what is behind it, which door of that to come out of, which way it is walked through."""
    return box(identity, name, "door", x, y, across, down,
               prop("door", into, "file"), prop("arrive", arrive), prop("direction", direction))


def light(identity, name, x, y, colour, radius, intensity, *extra, night=False):
    props = [prop("light_colour", colour, "color"), prop("light_radius", radius, "float"), prop("light_intensity", intensity, "float")]
    if night:
        props.append(prop("night", "true", "bool"))
    return point(identity, name, "light", x, y, *props, *extra)


def npc(identity, name, x, y, look, says, wanders=True, faces="down"):
    return point(identity, name, "npc", x, y, prop("look", look, "int"), prop("says", says), prop("wanders", "true" if wanders else "false", "bool"), prop("faces", faces))


def map_file(name, width, height, tileset, background, layers, objects, properties_text, next_layer, next_object):
    write(f"{name}.tmx", f"""
<?xml version="1.0" encoding="UTF-8"?>
<map version="1.10" tiledversion="1.11.0" orientation="orthogonal" renderorder="right-down" width="{width}" height="{height}" tilewidth="{T}" tileheight="{T}" infinite="0" backgroundcolor="{background}" nextlayerid="{next_layer}" nextobjectid="{next_object}">
 <properties>
{properties_text} </properties>
{tileset}{layers} <objectgroup id="{next_layer - 1}" name="things">
{objects} </objectgroup>
</map>
""")


# The sheets by cell, the numbers are columns and rows of their pictures (the previews in the packs have them).
# A cell that has nothing drawn in it is no tile at all, so a house stamped down brings no invisible tiles with
# it to bump into


class Sheet:
    def __init__(self, image, columns, first):
        self.columns, self.first = columns, first
        self.empty = set()
        path = os.path.join(OUT, image)
        if os.path.exists(path):
            picture = Image.open(path).convert("RGBA")
            for r in range(picture.height // T):
                for c in range(picture.width // T):
                    if picture.crop((c * T, r * T, c * T + T, r * T + T)).getextrema()[3][1] == 0:
                        self.empty.add((c, r))

    def __call__(self, c, r):
        return 0 if (c, r) in self.empty else self.first + r * self.columns + c


CAVE_FIRST = 1 + TOWN_COLUMNS * TOWN_ROWS

town = Sheet("town.png", TOWN_COLUMNS, 1)
cave_tile = Sheet("cave.png", CAVE_COLUMNS, CAVE_FIRST)
inside = Sheet("interior.png", INTERIOR_COLUMNS, 1)
lab_tile = Sheet("lab.png", LAB_COLUMNS, 1)
cave_inside = Sheet("cave.png", CAVE_COLUMNS, 1)


PINES = [(5, 1, 7), (6, 1, 7), (7, 1, 7), (8, 2, 6)]   # column, top row, how tall
HEDGE_TOP, HEDGE, HEDGE_BOTTOM = (21, 4), (21, 5), (21, 8)
FENCE_LEFT, FENCE, FENCE_RIGHT = (0, 1), (1, 1), (3, 1)
ROCK, FLOWERS, PEBBLE, CHEST, BARREL, BUSH, TUFT = (5, 8), (6, 8), (7, 8), (8, 8), (9, 8), (2, 5), (4, 9)
PATH_TOP, PATH_V, PATH_BOTTOM, PATH_LEFT, PATH_H, PATH_RIGHT, PATH_CROSS = (16, 8), (16, 9), (16, 12), (14, 10), (15, 10), (20, 10), (16, 10)


def write_village():
    """The town. 40 tiles by 30, pines all round it, the lab in the middle, a house either side, the cave up in the rocks."""
    width, height = 40, 30

    def pine(grid, x, y, which):
        column, top, tall = PINES[which % len(PINES)]
        for j in range(tall):
            grid.put(x, y + j, town(column, top + j))
        return tall

    # Grass under everything. The grass tile is 3 by 3 and tiles with itself, so the map is paved in 3 by 3
    # blocks of it. The other two grasses are a shade lighter, and a block of either stood out as a square once
    # the sun went down, so it is the one grass all over
    ground = Grid(width, height)

    def grass(x, y):
        return town(x % 3, 18 + y % 3)

    ground.fill(0, 0, width, height, grass)

    # Where the paths run. One tile wide, the lab's is two side by side
    def path_v(x, top, bottom):
        for y in range(top, bottom + 1):
            ground.put(x, y, town(*PATH_V))

    def path_h(left, right, y):
        for x in range(left, right + 1):
            ground.put(x, y, town(*PATH_H))

    path_h(6, 35, 19)
    ground.put(5, 19, town(*PATH_LEFT))
    ground.put(36, 19, town(*PATH_RIGHT))
    path_v(17, 15, 18)
    path_v(18, 15, 18)
    path_v(8, 17, 18)
    path_v(30, 17, 18)
    path_v(34, 6, 18)
    for x in (8, 17, 18, 30, 34):
        ground.put(x, 19, town(*PATH_CROSS))
    ground.put(34, 6, town(*PATH_TOP))

    # The water, the crops and the dirt the barrels stand on lie on the grass, their edges are see-through
    features = Grid(width, height)
    features.stamp(25, 20, town, 11, 8, 2, 4)      # the stream
    features.stamp(24, 24, town, 10, 12, 3, 2)     # into the pond
    features.stamp(10, 21, town, 6, 9, 5, 4)       # the crops
    features.stamp(29, 21, town, 0, 13, 6, 5)      # the walled garden
    features.stamp(20, 22, town, 1, 6, 3, 3)       # a patch of dirt
    features.stamp(2, 23, town, 6, 13, 4, 3)       # the little pond by the trees

    # What stands up, which is what throws shadows. The pines round the edge, the three buildings, the hedge,
    # the fences and the rocks
    things = Grid(width, height)
    for x in range(width):
        if 32 <= x <= 37:
            continue
        pine(things, x, 0, x)
    for x in range(width):
        pine(things, x, 26, x + 1)
    for y in (6, 11, 16):
        pine(things, 0, y, y)
        pine(things, 1, y + 2, y + 1)
        pine(things, 39, y, y + 2)
        pine(things, 38, y + 2, y)

    # The lab is the big house of the sheet, sign and all, except that the sign is a plain bit of wall now.
    # The house on either side is the narrow one, the same one twice
    things.stamp(15, 7, town, 9, 0, 9, 8)
    things.put(15, 12, town(9, 4))
    things.stamp(7, 8, town, 18, 0, 3, 9)
    things.stamp(29, 8, town, 18, 0, 3, 9)

    things.put(36, 8, town(*HEDGE_TOP))
    things.fill(36, 9, 1, 8, town(*HEDGE))
    things.put(36, 17, town(*HEDGE_BOTTOM))

    things.put(9, 20, town(*FENCE_LEFT))
    things.fill(10, 20, 5, 1, town(*FENCE))
    things.put(15, 20, town(*FENCE_RIGHT))

    for x, y, what in [(12, 17, ROCK), (26, 17, PEBBLE), (4, 21, ROCK), (21, 22, BARREL), (22, 23, BARREL), (37, 20, CHEST)]:
        things.put(x, y, town(*what))

    # The rocks the cave is in, out of the cave sheet. An arch of rock with the dark of the cave in it and
    # rounded rock round that
    cave = Grid(width, height)
    cave.stamp(32, 2, cave_tile, 1, 1, 2, 2)
    cave.stamp(34, 2, cave_tile, 4, 1, 2, 2)
    cave.stamp(36, 2, cave_tile, 1, 1, 2, 2)
    cave.put(37, 2, cave_tile(1, 1) | H)
    cave.put(37, 3, cave_tile(1, 2) | H)
    cave.put(32, 4, cave_tile(1, 4))
    cave.put(32, 5, cave_tile(1, 5))
    cave.put(33, 4, cave_tile(10, 4))
    cave.put(33, 5, cave_tile(10, 5))
    cave.stamp(34, 4, cave_tile, 10, 1, 2, 2)
    cave.put(36, 4, cave_tile(11, 4))
    cave.put(36, 5, cave_tile(11, 5))
    cave.put(37, 4, cave_tile(1, 4) | H)
    cave.put(37, 5, cave_tile(1, 5) | H)

    # The flowers and the bushes, in front of the rest and nothing to bump into
    decor = Grid(width, height)
    for x, y in [(11, 16), (13, 15), (25, 16), (27, 15), (5, 18), (33, 21), (22, 17), (12, 26), (27, 26), (3, 19)]:
        decor.put(x, y, town(*FLOWERS))
    for x, y in [(14, 17), (21, 16), (31, 22), (16, 25), (8, 26), (24, 14), (6, 22)]:
        decor.put(x, y, town(*TUFT))
    for x, y in [(4, 15), (35, 13), (19, 25), (10, 25), (26, 12)]:
        decor.put(x, y, town(*BUSH))

    # What is bumped into, every cell of the things that stand up, the water and the garden, apart from the
    # doorways, which have to be walked into. Hidden, it is there to be asked and not to be seen
    solid = Grid(width, height)
    doors = {(8, 16), (8, 17), (30, 16), (30, 17), (17, 13), (18, 13), (17, 14), (18, 14), (34, 5), (35, 5), (34, 6), (35, 6)}
    for y in range(height):
        for x in range(width):
            if (x, y) in doors:
                continue
            if things.any(x, y) or cave.any(x, y):
                solid.put(x, y, town(*ROCK))
    for x in range(25, 27):
        for y in range(20, 24):
            solid.put(x, y, town(*ROCK))
    solid.fill(24, 24, 3, 2, town(*ROCK))
    solid.fill(2, 23, 4, 3, town(*ROCK))
    solid.fill(10, 22, 5, 3, town(*ROCK))
    solid.fill(29, 21, 6, 5, town(*ROCK))

    objects = ""
    # A door is the doorway and the step in front of it, see TileMapRooms for why it is two tiles tall
    objects += door(1, "house_a", 8, 16, 1, 2, "house_a.tmx", "out", "up")
    objects += door(2, "house_b", 30, 16, 1, 2, "house_b.tmx", "out", "up")
    objects += door(3, "lab", 17, 13, 2, 2, "lab.tmx", "out", "up")
    objects += door(4, "cave", 34, 5, 2, 2, "cave.tmx", "out", "up")
    objects += point(5, "player", "spawn", 18, 18)

    # The windows, which only light up at night. Each is a warm light a touch below its window, in front of
    # the wall, with a glow of its own so the window is seen to be lit
    windows = [(16, 12), (20, 12), (22, 10), (8, 13), (30, 13), (16, 10), (20, 10)]
    identity = 6
    for x, y in windows:
        objects += light(identity, f"window {identity - 5}", x, y, "#ffffc070", 56, 0.65, prop("light_glow", "0.45", "float"), prop("light_shadows", "false", "bool"), night=True)
        identity += 1

    # Who is who on the villagers' sheet, a row each. 0 is the bearded man, 1 the woman, 2 the girl, 3 the boy
    objects += npc(identity, "gardener", 12, 25, 1, "The crops do better since the lab put the lights in. Don't ask me how."); identity += 1
    objects += npc(identity, "walker", 22, 15, 3, "Lovely evening for it. Mind the pond, the path goes round."); identity += 1
    objects += npc(identity, "kid", 3, 21, 2, "I'm not allowed in the cave. You could go and tell me what's in it!"); identity += 1
    objects += npc(identity, "elder", 4, 13, 0, "When I was your age the sun came up on the other side. Or I did."); identity += 1

    objects += box(identity, "town", "zone", 0, 0, width, height, prop("says", "Hometown")); identity += 1
    objects += box(identity, "pond", "zone", 23, 19, 5, 8, prop("says", "The pond")); identity += 1
    objects += box(identity, "rocks", "zone", 31, 1, 8, 6, prop("says", "The rocks")); identity += 1

    layers = (layer(1, "ground", ground)
              + layer(2, "features", features)
              + layer(3, "things", things, "", [("CastsShadows", "bool", "true")])
              + layer(4, "rocks", cave, "", [("CastsShadows", "bool", "true")])
              + layer(5, "decor", decor)
              + layer(6, "solid", solid, ' visible="0"', [("IsCollidable", "bool", "true")]))

    tilesets = (f' <tileset firstgid="1" source="town.tsx"/>\n'
                f' <tileset firstgid="{CAVE_FIRST}" source="cave.tsx"/>\n')

    map_file("village", width, height, tilesets, "#0b1a0b", layers, objects,
             prop("name", "Hometown") + prop("ambient", "#ff404a60", "color") + prop("GeometryOcclusion", "true", "bool") + prop("WallHeight", "40", "float"),
             7, identity)


# The interior sheet by cell


WALL_TOP, WALL = [(0, 0), (1, 0), (2, 0)], [(0, 1), (1, 1), (2, 1)]
FLOOR_WOOD, FLOOR_PLAIN, FLOOR_CHECK, FLOOR_STONE = (3, 9), (0, 9), (4, 9), (3, 8)
DOORMAT, WINDOW, SMALL_WINDOW, SKIRTING = (3, 7), (4, 7), (5, 7), (6, 7)


def write_house(name, title, floor_tile, furniture, people, lamp_at, says_where):
    """A house, 12 tiles by 9. Two rows of wall along the top, a floor, the doormat at the bottom in the middle and
    the door object on it, which is the way out. What is in it is handed in."""
    width, height = 12, 9

    floor = Grid(width, height)
    floor.fill(0, 2, width, height - 2, inside(*floor_tile))
    floor.put(5, 8, inside(*DOORMAT))

    # The back wall, with a window either side
    wall = Grid(width, height)
    for x in range(width):
        wall.put(x, 0, inside(*WALL_TOP[0 if x == 0 else 2 if x == width - 1 else 1]))
        wall.put(x, 1, inside(*WALL[0 if x == 0 else 2 if x == width - 1 else 1]))
    wall.put(2, 1, inside(*WINDOW))
    wall.put(9, 1, inside(*WINDOW))

    stuff = Grid(width, height)
    solid = Grid(width, height)
    for x, y, sheet_x, sheet_y, across, down in furniture:
        stuff.stamp(x, y, inside, sheet_x, sheet_y, across, down)
        solid.fill(x, y, across, down, inside(*FLOOR_STONE))

    # The walls are walked into as well, and the edges of the room, a house has no door in its side and
    # nobody walks out of the bottom of one except over the mat
    solid.fill(0, 0, width, 2, inside(*FLOOR_STONE))
    solid.fill(0, 2, 1, height - 2, inside(*FLOOR_STONE))
    solid.fill(width - 1, 2, 1, height - 2, inside(*FLOOR_STONE))
    solid.fill(0, height - 1, width, 1, inside(*FLOOR_STONE))
    solid.put(5, height - 1, 0)

    objects = door(1, "out", 5, 7, 1, 2, "village.tmx", name, "down")
    objects += light(2, "lamp", lamp_at[0], lamp_at[1], "#ffffd0a0", 150, 1.0, prop("light_glow", "0.3", "float"), prop("light_height", "70", "float"))
    identity = 3
    for x, y, look, says, faces in people:
        objects += npc(identity, f"{name} {identity}", x, y, look, says, wanders=False, faces=faces)
        identity += 1
    objects += box(identity, "room", "zone", 0, 0, width, height, prop("says", says_where))
    identity += 1

    layers = (layer(1, "floor", floor)
              + layer(2, "wall", wall, "", [("CastsShadows", "bool", "true")])
              + layer(3, "furniture", stuff, "", [("CastsShadows", "bool", "true")])
              + layer(4, "solid", solid, ' visible="0"', [("IsCollidable", "bool", "true")]))

    map_file(name, width, height, ' <tileset firstgid="1" source="interior.tsx"/>\n', "#000000", layers, objects,
             prop("name", title) + prop("ambient", "#ff4a4238", "color") + prop("GeometryOcclusion", "true", "bool") + prop("WallHeight", "12", "float"),
             5, identity)


def write_houses():
    # A bed, a bookcase, a telly on its stand, a table with chairs, a plant, a clock on the wall
    # Nothing stands in the two tiles over the doormat, that is where whoever comes in is put down
    write_house("house_a", "Home", FLOOR_WOOD, [
        (9, 2, 5, 1, 2, 2),     # the double bed, against the back wall
        (0, 2, 1, 2, 2, 2),     # the big bookcase
        (3, 2, 8, 3, 1, 1),     # a telly
        (7, 3, 5, 3, 2, 3),     # the table with the cloth on it
        (6, 4, 3, 4, 1, 1),     # a chair on the left
        (9, 4, 3, 5, 1, 1),     # and one on the right
        (11, 4, 7, 4, 1, 2),    # a plant in the corner
        (0, 6, 7, 1, 1, 2),     # a single bed under the window
    ], [(3, 3, 1, "You're back! Dinner's on. Go and see what the professor wants first, he's been pacing.", "down")],
        (6, 3), "Home")

    # A sparser one, the neighbour has boxes everywhere and a radio
    write_house("house_b", "The neighbour's", FLOOR_CHECK, [
        (0, 2, 8, 1, 3, 2),     # boxes, three of them
        (10, 2, 7, 1, 1, 2),    # a single bed
        (4, 2, 4, 1, 1, 2),     # the radio on its cabinet
        (6, 2, 3, 1, 1, 2),     # the dresser with the vase
        (3, 5, 9, 4, 2, 1),     # a flat telly on the floor, as you do
        (8, 6, 10, 5, 1, 1),    # books left on the floor
        (0, 7, 0, 4, 1, 2),     # an empty shelf
        (11, 6, 7, 4, 1, 2),    # a plant
    ], [(7, 4, 0, "Still unpacking. Nine years, and still unpacking.", "down")],
        (6, 3), "The neighbour's")


# The lab sheet by cell


def write_lab():
    """The lab, 16 by 11. A big dark screen along the back wall framed in blue, consoles under it, and a blue floor
    with the yellow line down the middle to the door."""
    width, height = 16, 11

    floor = Grid(width, height)

    # A plain blue floor, with the hatch pattern in the corners and the yellow line down the middle
    floor.fill(0, 0, width, height, lambda x, y: lab_tile(1 + x % 2, 10 + y % 2))
    floor.fill(0, 7, 2, 2, lambda x, y: lab_tile(3 + (x % 2), 10 + (y % 2)))
    floor.fill(width - 2, 7, 2, 2, lambda x, y: lab_tile(3 + (x % 2), 10 + (y % 2)))
    for y in range(4, height):
        floor.put(7, y, lab_tile(10, 8 + (y % 2)))
        floor.put(8, y, lab_tile(10, 8 + (y % 2)))

    # The back wall. The screen is the big dark window of the sheet with its frame, the wall under it the olive
    # band with the lamps in it, and the olive wall with the pipes either side of that
    wall = Grid(width, height)
    wall.stamp(0, 0, lab_tile, 8, 1, 6, 2)
    wall.stamp(10, 0, lab_tile, 8, 1, 6, 2)
    wall.stamp(4, 0, lab_tile, 1, 1, 6, 4)
    wall.stamp(0, 2, lab_tile, 14, 1, 2, 2)
    wall.stamp(2, 2, lab_tile, 17, 1, 2, 2)
    wall.stamp(12, 2, lab_tile, 17, 1, 2, 2)
    wall.stamp(14, 2, lab_tile, 14, 1, 2, 2)

    # The doorway on the right of the back wall is a decoration, nobody goes through it
    wall.stamp(10, 1, lab_tile, 17, 4, 2, 3)

    # What glows by itself, the consoles' screens. The tracer takes them for lamps
    screens = Grid(width, height)
    screens.stamp(0, 2, lab_tile, 14, 1, 2, 2)
    screens.stamp(14, 2, lab_tile, 14, 1, 2, 2)

    solid = Grid(width, height)
    solid.fill(0, 0, width, 4, lab_tile(3, 10))
    solid.fill(0, 7, 2, 2, lab_tile(3, 10))
    solid.fill(width - 2, 7, 2, 2, lab_tile(3, 10))

    # The edges of the room, with the way out in the bottom one
    solid.fill(0, 4, 1, height - 4, lab_tile(3, 10))
    solid.fill(width - 1, 4, 1, height - 4, lab_tile(3, 10))
    solid.fill(0, height - 1, width, 1, lab_tile(3, 10))
    solid.fill(7, height - 1, 2, 1, 0)

    objects = door(1, "out", 7, 9, 2, 2, "village.tmx", "lab", "down")
    objects += light(2, "ceiling", 8, 5, "#ffd0e8ff", 190, 0.72, prop("light_glow", "0.3", "float"), prop("light_height", "90", "float"))
    objects += light(3, "screen", 1, 3, "#ff60f0ff", 70, 0.7, prop("light_flicker", "0.25", "float"), prop("light_shadows", "false", "bool"), prop("light_glow", "0.4", "float"))
    objects += light(4, "screen 2", 14, 3, "#ff60f0ff", 70, 0.7, prop("light_flicker", "0.25", "float"), prop("light_shadows", "false", "bool"), prop("light_glow", "0.4", "float"))
    objects += npc(5, "professor", 8, 6, 0, "Ah, there you are. The shadows outside move with the sun now, go and have a look. Then come back, I have a thing for you.", wanders=False, faces="down")
    objects += npc(6, "assistant", 3, 8, 3, "Don't touch the big screen. Nobody knows what it's for.", wanders=True)
    objects += box(7, "room", "zone", 0, 0, width, height, prop("says", "The lab"))

    layers = (layer(1, "floor", floor)
              + layer(2, "wall", wall, "", [("CastsShadows", "bool", "true")])
              + layer(3, "screens", screens, "", [("Emissive", "float", "1")])
              + layer(4, "solid", solid, ' visible="0"', [("IsCollidable", "bool", "true")]))

    map_file("lab", width, height, ' <tileset firstgid="1" source="lab.tsx"/>\n', "#000000", layers, objects,
             prop("name", "The lab") + prop("ambient", "#ff1e2636", "color") + prop("GeometryOcclusion", "true", "bool") + prop("WallHeight", "24", "float"),
             5, 8)


def write_cave():
    """The cave, 14 by 10. Mossy floor with rock all round it, the way out in the bottom wall."""
    width, height = 14, 10

    floor = Grid(width, height)
    floor.fill(0, 0, width, height, lambda x, y: cave_inside(7 + x % 2, 1 + y % 2))
    floor.fill(3, 3, 4, 3, lambda x, y: cave_inside(4 + x % 2, 4 + y % 2))
    floor.fill(8, 5, 2, 2, lambda x, y: cave_inside(7 + x % 2, 4 + y % 2))

    # The rock, along the top and the bottom and down the sides, the corners rounded
    rock = Grid(width, height)
    for x in range(0, width, 2):
        rock.stamp(x, 0, cave_inside, 4, 1, 2, 2)
        rock.stamp(x, height - 2, cave_inside, 4, 8, 2, 2)
    rock.stamp(0, 0, cave_inside, 1, 1, 2, 2)
    rock.put(width - 2, 0, cave_inside(1, 1) | H)
    rock.put(width - 1, 0, cave_inside(1, 1) | H)
    rock.stamp(0, height - 2, cave_inside, 1, 8, 2, 2)
    for y in range(2, height - 2):
        rock.put(0, y, cave_inside(1, 4 + y % 2))
        rock.put(width - 1, y, cave_inside(1, 4 + y % 2) | H)
    rock.stamp(6, height - 2, cave_inside, 7, 8, 2, 2)

    # A rock in the middle to walk round, and the way out, a gap in the rock at the bottom
    rock.stamp(9, 3, cave_inside, 10, 4, 2, 2)
    rock.fill(6, height - 2, 2, 2, 0)

    solid = Grid(width, height)
    for y in range(height):
        for x in range(width):
            if rock.any(x, y):
                solid.put(x, y, cave_inside(4, 4))

    objects = door(1, "out", 6, 8, 2, 2, "village.tmx", "cave", "down")
    # Low to the ground, they are things that glow and not lamps, so everything throws long from them
    objects += light(2, "glow", 4, 4, "#ff80ffb0", 90, 0.8, prop("light_flicker", "0.1", "float"), prop("light_glow", "0.7", "float"), prop("light_height", "12", "float"))
    objects += light(3, "glow 2", 10, 6, "#ff80d0ff", 70, 0.6, prop("light_flicker", "0.15", "float"), prop("light_glow", "0.7", "float"), prop("light_height", "12", "float"))
    objects += npc(4, "hermit", 11, 7, 2, "Shh. The glowing bits are shy.", wanders=False, faces="left")
    objects += box(5, "room", "zone", 0, 0, width, height, prop("says", "The cave"))

    layers = (layer(1, "floor", floor)
              + layer(2, "rock", rock, "", [("CastsShadows", "bool", "true")])
              + layer(3, "solid", solid, ' visible="0"', [("IsCollidable", "bool", "true")]))

    map_file("cave", width, height, ' <tileset firstgid="1" source="cave.tsx"/>\n', "#000000", layers, objects,
             prop("name", "The cave") + prop("ambient", "#ff141c20", "color") + prop("GeometryOcclusion", "true", "bool") + prop("WallHeight", "40", "float"),
             4, 6)


def main():
    global town, cave_tile, inside, lab_tile, cave_inside

    os.makedirs(OUT, exist_ok=True)
    if len(sys.argv) == 3:
        make_sheets(sys.argv[1], sys.argv[2])
        town = Sheet("town.png", TOWN_COLUMNS, 1)
        cave_tile = Sheet("cave.png", CAVE_COLUMNS, CAVE_FIRST)
        inside = Sheet("interior.png", INTERIOR_COLUMNS, 1)
        lab_tile = Sheet("lab.png", LAB_COLUMNS, 1)
        cave_inside = Sheet("cave.png", CAVE_COLUMNS, 1)
    elif len(sys.argv) != 1:
        print(__doc__)
        return

    make_maps()
    write_tilesets()
    write_village()
    write_houses()
    write_lab()
    write_cave()
    print(f"written to {os.path.abspath(OUT)}")


if __name__ == "__main__":
    main()
