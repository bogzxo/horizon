"""Paints AutoVoxel's block atlas, content/atlas.png, sixteen tiles of sixteen pixels a row.

Every tile is made out of noise and a palette, seeded by its name, so running this again gives the same picture.
The order of TILES is the tile numbers Blocks/Blocks.cs uses, add new ones at the end or every block moves.

    python Art/make_atlas.py
"""
import os
import zlib

import numpy as np
from PIL import Image

T = 16
PER_ROW = 16
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "content", "atlas.png")


def rng(name):
    return np.random.default_rng(zlib.crc32(name.encode()))


def smooth_noise(r, scale=4, octaves=2):
    """Value noise that wraps round the tile, so a wall of blocks has no seams."""
    out = np.zeros((T, T))
    amp, total = 1.0, 0.0
    for o in range(octaves):
        n = max(2, T // (scale >> o if scale >> o else 1))
        grid = r.random((n, n))
        ys = np.arange(T) * n / T
        y0 = np.floor(ys).astype(int)
        fy = ys - y0
        layer = np.zeros((T, T))
        for i in range(T):
            for j in range(T):
                a = grid[y0[i] % n, y0[j] % n]
                b = grid[y0[i] % n, (y0[j] + 1) % n]
                c = grid[(y0[i] + 1) % n, y0[j] % n]
                d = grid[(y0[i] + 1) % n, (y0[j] + 1) % n]
                u, v = fy[j], fy[i]
                u, v = u * u * (3 - 2 * u), v * v * (3 - 2 * v)
                layer[i, j] = (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v
        out += layer * amp
        total += amp
        amp *= 0.5
    return out / total


def palette_tile(name, colors, scale=4, octaves=2, grain=0.35):
    """Noise through a palette, dark to light, with some per pixel grain."""
    r = rng(name)
    n = smooth_noise(r, scale, octaves) * (1 - grain) + r.random((T, T)) * grain
    colors = np.array(colors, dtype=float)
    idx = np.clip((n * len(colors)).astype(int), 0, len(colors) - 1)
    img = colors[idx]
    return np.concatenate([img, np.full((T, T, 1), 255.0)], axis=2)


def speckle(img, name, color, chance):
    r = rng(name + "speckle")
    mask = r.random((T, T)) < chance
    img[mask, :3] = color
    return img


def stones(name, base, mortar, cells=6, jitter=40):
    """Rounded stones with mortar between them, a Voronoi of a few points."""
    r = rng(name)
    pts = r.random((cells, 2)) * T
    shades = r.integers(-jitter, jitter, cells)
    img = np.zeros((T, T, 4))
    img[..., 3] = 255
    for y in range(T):
        for x in range(T):
            d = []
            for (py, px) in pts:
                dy = min(abs(y - py), T - abs(y - py))
                dx = min(abs(x - px), T - abs(x - px))
                d.append(dy * dy + dx * dx)
            order = np.argsort(d)
            gap = np.sqrt(d[order[1]]) - np.sqrt(d[order[0]])
            if gap < 1.1:
                img[y, x, :3] = mortar
            else:
                shade = shades[order[0]] + r.integers(-10, 10)
                light = 8 if (y + x) % 7 == 0 else 0
                img[y, x, :3] = np.clip(np.array(base) + shade + light, 0, 255)
    return img


def band_top(img, name, color_dark, color_light, depth=4):
    """A layer over the top of a side tile that hangs down unevenly, grass over dirt, snow over dirt."""
    r = rng(name + "band")
    for x in range(T):
        d = depth + r.integers(-1, 2) + (1 if r.random() < 0.25 else 0)
        for y in range(d):
            img[y, x, :3] = color_light if r.random() < 0.55 else color_dark
    return img


def grass_top():
    return palette_tile("grass_top", [(74, 120, 40), (88, 138, 48), (98, 152, 54), (112, 166, 62), (126, 178, 72)], 4, 2, 0.5)


def grass_side():
    img = dirt()
    return band_top(img, "grass_side", (82, 132, 44), (106, 158, 58))


def dirt():
    img = palette_tile("dirt", [(104, 74, 50), (118, 85, 58), (128, 92, 62), (138, 100, 68)], 4, 2, 0.5)
    return speckle(img, "dirt", (150, 130, 112), 0.04)


def stone():
    img = palette_tile("stone", [(108, 108, 110), (118, 118, 120), (126, 126, 128), (134, 134, 136), (144, 144, 146)], 8, 3, 0.25)
    r = rng("stone cracks")
    for _ in range(3):
        y, x = r.integers(0, T, 2)
        for k in range(r.integers(2, 5)):
            img[(y + k) % T, (x + (k // 2)) % T, :3] = (92, 92, 94)
    return img


def cobblestone():
    return stones("cobble", (122, 122, 124), (70, 70, 72), cells=7)


def sand():
    img = palette_tile("sand", [(214, 200, 150), (222, 208, 158), (228, 216, 166), (234, 222, 174)], 4, 2, 0.6)
    return speckle(img, "sand", (196, 180, 130), 0.05)


def gravel():
    return stones("gravel", (128, 120, 116), (92, 86, 84), cells=11, jitter=34)


def snow():
    return palette_tile("snow", [(232, 238, 246), (240, 245, 252), (246, 250, 255)], 4, 2, 0.3)


def snow_side():
    img = dirt()
    return band_top(img, "snow_side", (226, 232, 240), (244, 248, 255), depth=5)


def log_side(name, colors, stripe):
    r = rng(name)
    img = palette_tile(name, colors, 4, 1, 0.3)
    for x in range(T):
        if r.random() < 0.3:
            for y in range(T):
                if r.random() < 0.8:
                    img[y, x, :3] = stripe
    return img


def oak_log_side():
    return log_side("oak_log", [(92, 70, 44), (104, 80, 50), (112, 86, 54)], (74, 56, 34))


def log_top(name, outer, ring_a, ring_b):
    img = np.zeros((T, T, 4))
    img[..., 3] = 255
    c = (T - 1) / 2
    for y in range(T):
        for x in range(T):
            d = max(abs(x - c), abs(y - c))
            if d > 6.5:
                img[y, x, :3] = outer
            else:
                img[y, x, :3] = ring_a if int(d) % 2 == 0 else ring_b
    return img


def oak_log_top():
    return log_top("oak_top", (96, 74, 46), (168, 132, 84), (150, 116, 72))


def birch_log_side():
    r = rng("birch")
    img = palette_tile("birch", [(214, 212, 204), (226, 224, 218), (236, 234, 228)], 4, 1, 0.2)
    for _ in range(7):
        y, x = r.integers(0, T, 2)
        w = r.integers(2, 5)
        for k in range(w):
            img[y, (x + k) % T, :3] = (40, 38, 34)
    return img


def spruce_log_side():
    return log_side("spruce_log", [(58, 42, 28), (66, 48, 32), (74, 54, 36)], (46, 32, 22))


def planks():
    r = rng("planks")
    img = palette_tile("planks", [(156, 122, 76), (166, 130, 82), (176, 138, 88)], 8, 1, 0.3)
    for y in (3, 7, 11, 15):
        img[y, :, :3] = (110, 84, 52)
    for row, x in enumerate(r.integers(0, T, 4)):
        for y in range(row * 4, row * 4 + 3):
            img[y, x, :3] = (120, 92, 58)
    return img


def water():
    return palette_tile("water", [(34, 84, 160), (40, 94, 172), (48, 104, 184)], 4, 2, 0.2)


def glass():
    img = np.zeros((T, T, 4))
    img[..., :3] = (200, 228, 240)
    img[..., 3] = 60
    img[0, :, 3] = img[-1, :, 3] = img[:, 0, 3] = img[:, -1, 3] = 255
    return img


def leaves(name, colors):
    img = palette_tile(name, colors, 4, 2, 0.6)
    return speckle(img, name, tuple(int(c * 0.6) for c in colors[0]), 0.08)


def sandstone():
    img = palette_tile("sandstone", [(208, 190, 136), (216, 198, 144), (222, 206, 152)], 8, 1, 0.3)
    for y in (4, 9, 13):
        img[y, :, :3] = (186, 168, 116)
    return img


def sandstone_top():
    return palette_tile("sandstone_top", [(214, 198, 144), (222, 206, 152), (228, 212, 160)], 4, 2, 0.25)


def clay():
    return palette_tile("clay", [(150, 156, 170), (158, 164, 178), (164, 170, 184)], 4, 2, 0.25)


def ice():
    img = palette_tile("ice", [(150, 190, 236), (162, 200, 242), (176, 210, 248)], 8, 2, 0.2)
    r = rng("ice cracks")
    for _ in range(4):
        y, x = r.integers(0, T, 2)
        for k in range(r.integers(3, 6)):
            img[(y + k) % T, (x + k) % T, :3] = (210, 230, 255)
    return img


def bricks():
    img = np.zeros((T, T, 4))
    img[..., 3] = 255
    r = rng("bricks")
    for y in range(T):
        for x in range(T):
            row = y // 4
            off = 4 if row % 2 else 0
            if y % 4 == 3 or (x + off) % 8 == 7:
                img[y, x, :3] = (172, 164, 156)
            else:
                shade = r.integers(-12, 12)
                img[y, x, :3] = np.clip(np.array((150, 70, 54)) + shade, 0, 255)
    return img


def glowstone():
    r = rng("glow")
    img = stones("glowstone", (236, 196, 110), (150, 110, 60), cells=8, jitter=24)
    return speckle(img, "glow", (255, 240, 190), 0.12)


def ore(name, color):
    img = stone()
    r = rng(name)
    for _ in range(4):
        y, x = r.integers(1, T - 2, 2)
        for dy, dx in ((0, 0), (0, 1), (1, 0), (1, 1)):
            if r.random() < 0.85:
                img[y + dy, x + dx, :3] = np.clip(np.array(color) + r.integers(-18, 18), 0, 255)
    return img


def lava():
    return palette_tile("lava", [(190, 54, 10), (220, 90, 14), (240, 130, 20), (252, 176, 40), (255, 214, 90)], 4, 3, 0.15)


def mossy_cobble():
    img = cobblestone()
    r = rng("moss")
    mask = smooth_noise(r, 4, 2) > 0.55
    img[mask, :3] = (86, 120, 60)
    return img


def deepslate():
    img = palette_tile("deepslate", [(62, 62, 68), (70, 70, 76), (78, 78, 84)], 8, 2, 0.35)
    for y in (2, 7, 12):
        img[y, ::2, :3] = (54, 54, 60)
    return img


def granite():
    img = palette_tile("granite", [(150, 104, 86), (164, 114, 94), (176, 124, 102)], 4, 2, 0.6)
    return speckle(img, "granite", (200, 160, 146), 0.08)


def cactus_side():
    img = palette_tile("cactus", [(52, 120, 40), (60, 132, 46), (68, 142, 52)], 8, 1, 0.2)
    for x in (2, 7, 12):
        img[:, x, :3] = (40, 96, 32)
    for y in range(1, T, 4):
        img[y, 4, :3] = img[y + 2 if y + 2 < T else y, 10, :3] = (230, 226, 180)
    return img


def cactus_top():
    img = palette_tile("cactus_top", [(70, 140, 52), (80, 152, 60)], 4, 1, 0.2)
    img[0, :, :3] = img[-1, :, :3] = img[:, 0, :3] = img[:, -1, :3] = (44, 100, 34)
    return img


def red_sand():
    return palette_tile("red_sand", [(176, 90, 44), (188, 98, 50), (198, 106, 56)], 4, 2, 0.6)


def terracotta():
    return palette_tile("terracotta", [(150, 86, 62), (158, 92, 66), (164, 98, 70)], 8, 1, 0.3)


def dry_grass_top():
    return palette_tile("dry_grass", [(146, 150, 70), (160, 160, 78), (172, 168, 86), (184, 178, 96)], 4, 2, 0.5)


def dry_grass_side():
    img = dirt()
    return band_top(img, "dry_side", (146, 148, 68), (170, 166, 84))


def lantern():
    img = np.zeros((T, T, 4))
    img[..., 3] = 255
    img[..., :3] = (60, 46, 30)
    img[2:14, 2:14, :3] = (255, 196, 110)
    img[4:12, 4:12, :3] = (255, 226, 160)
    img[7:9, :, :3] = (60, 46, 30)
    img[:, 7:9, :3] = (60, 46, 30)
    return img


def bedrock():
    return palette_tile("bedrock", [(30, 30, 32), (54, 54, 56), (84, 84, 86), (110, 110, 112)], 4, 1, 0.7)


def podzol():
    return palette_tile("podzol", [(90, 62, 36), (106, 74, 42), (120, 88, 48), (96, 110, 46)], 4, 2, 0.6)


TILES = [
    ("grass_top", grass_top), ("grass_side", grass_side), ("dirt", dirt), ("stone", stone),
    ("cobblestone", cobblestone), ("sand", sand), ("gravel", gravel), ("snow", snow),
    ("snow_side", snow_side), ("oak_log", oak_log_side), ("oak_log_top", oak_log_top), ("birch_log", birch_log_side),
    ("spruce_log", spruce_log_side), ("planks", planks), ("water", water), ("glass", glass),
    ("oak_leaves", lambda: leaves("oak_leaves", [(48, 98, 30), (58, 112, 36), (68, 124, 42)])),
    ("birch_leaves", lambda: leaves("birch_leaves", [(88, 132, 50), (100, 146, 58), (112, 158, 66)])),
    ("spruce_leaves", lambda: leaves("spruce_leaves", [(34, 70, 42), (40, 80, 48), (48, 90, 54)])),
    ("sandstone", sandstone), ("sandstone_top", sandstone_top), ("clay", clay), ("ice", ice), ("bricks", bricks),
    ("glowstone", glowstone), ("coal_ore", lambda: ore("coal", (36, 36, 38))), ("iron_ore", lambda: ore("iron", (200, 160, 126))),
    ("gold_ore", lambda: ore("gold", (240, 210, 70))), ("diamond_ore", lambda: ore("diamond", (96, 226, 220))),
    ("lava", lava), ("mossy_cobblestone", mossy_cobble), ("deepslate", deepslate), ("granite", granite),
    ("cactus", cactus_side), ("cactus_top", cactus_top), ("red_sand", red_sand), ("terracotta", terracotta),
    ("dry_grass_top", dry_grass_top), ("dry_grass_side", dry_grass_side), ("lantern", lantern), ("bedrock", bedrock),
    ("podzol", podzol),
]


def main():
    atlas = np.zeros((T * PER_ROW, T * PER_ROW, 4), dtype=np.uint8)
    for i, (name, make) in enumerate(TILES):
        tile = np.clip(make(), 0, 255).astype(np.uint8)
        y, x = divmod(i, PER_ROW)
        atlas[y * T:(y + 1) * T, x * T:(x + 1) * T] = tile
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    Image.fromarray(atlas, "RGBA").save(OUT)
    for i, (name, _) in enumerate(TILES):
        print(f"{i:3d} {name}")


if __name__ == "__main__":
    main()
