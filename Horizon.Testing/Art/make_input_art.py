#!/usr/bin/env python3
"""Paints the art of the keyboard and mouse example into Assets/examples/input, a ring, a disc and a block, all
white so they can be tinted any colour and soft round the edges so they don't look like arse when they're scaled.
Where each one is has to match art.hor next to it.

usage  python Art/make_input_art.py        from Horizon.Testing
"""
import math
import os

from PIL import Image

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "examples", "input")

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

os.makedirs(OUT, exist_ok=True)
image.save(os.path.join(OUT, "art.png"))
print("wrote", os.path.normpath(os.path.join(OUT, "art.png")))
