#!/usr/bin/env python3
"""Paints the bits of white art a couple of examples tint and stretch, into Assets/examples. All of it white on
see-through so a tint makes it any colour, and soft round the edges so it doesn't look like arse when it's scaled.

usage  python Art/make_small_art.py        from Horizon.Testing

  input/art.png          a ring, a disc and a block, for the keyboard and mouse example (art.hor says where each is)
  transitions/art.png    two 64 pixel cells side by side, a soft glow and a crisp disc, for the scenes example
"""
import math
import os

from PIL import Image

ASSETS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "examples")


def save(image, *where):
    path = os.path.join(ASSETS, *where)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    image.save(path)
    print("wrote", os.path.normpath(path))


def input_art():
    image = Image.new("RGBA", (256, 128))
    pixels = image.load()

    for y in range(128):
        for x in range(128):
            # A ring 7 pixels thick just inside the edge, with a pixel of fade either side
            distance = math.hypot(x + 0.5 - 64.0, y + 0.5 - 64.0)
            ring = max(0.0, min(1.0, 1.0 - (abs(distance - 58.0) - 3.5)))
            pixels[x, y] = (255, 255, 255, int(ring * 255.0))

    for y in range(64):
        for x in range(64):
            distance = math.hypot(x + 0.5 - 32.0, y + 0.5 - 32.0)
            pixels[128 + x, y] = (255, 255, 255, int(max(0.0, min(1.0, 30.0 - distance)) * 255.0))

    # The block is 8 square and only the middle 4 of it are used, so blending at the edges never picks up nothing
    for y in range(8):
        for x in range(8):
            pixels[192 + x, y] = (255, 255, 255, 255)

    save(image, "input", "art.png")


def transitions_art():
    cell = 64
    middle = cell / 2.0
    image = Image.new("RGBA", (cell * 2, cell))
    pixels = image.load()

    for y in range(cell):
        for x in range(cell):
            away = math.hypot(x + 0.5 - middle, y + 0.5 - middle) / middle

            # Both fade out before the edge of their cell. The texture is smoothed, so anything touching the edge
            # bleeds into the cell next door and there is a sliver of disc at the edge of every glow
            glow = max(0.0, 1.0 - away / 0.95) ** 2
            disc = max(0.0, min(1.0, (0.94 - away) * middle))
            pixels[x, y] = (255, 255, 255, int(glow * 255.0))
            pixels[cell + x, y] = (255, 255, 255, int(disc * 255.0))

    save(image, "transitions", "art.png")


input_art()
transitions_art()
