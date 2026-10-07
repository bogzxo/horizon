#version 410 core

// The look of a picture tube: soft dots along scan lines and glass that bulges, without a shadow mask. A scan line
// is two beams here. The narrow one is what draws the line, and what it leaves dark between itself and the next
// would cost the picture its brightness: so it is driven as hard as it can go without burning out, and whatever
// light is still missing after that goes into a wide beam that fills the gaps. Dark colours keep crisp lines,
// bright ones bloom into each other, and all of them come out as bright as they went in.
// After the CRT shader Timothy Lottes put in the public domain, in the version with the beams above. This does
// what that does for less:
//
//   - That one reads the picture 11 times for every pixel (three, five and three dots of three scan lines), turns
//     each read from sRGB into linear light with a power a channel and checks each against the edge of the glass.
//     This one reads it 7 times. The picture comes in already shrunk to the size of the tube, in a texture that
//     hands back linear light and is black outside of its edges (see crt_shrink.frag), and a read that lands between
//     two dots is the GPU blending the two by how close it is to each: put in the right spot, one read is two dots
//     in the right proportions. Only along a line though. The lines have to stay apart, how hard each is driven
//     goes by its own colour.
//   - How much light a beam gives over a whole line (which is what the driving goes by) is worked out once a frame
//     by the effect, rather than a root for every pixel.

layout(location = 0) in vec2 texCoords;

// The picture at the size of the tube, in linear light.
uniform sampler2D uSource;

// How many dots a scan line has, and how many scan lines there are.
uniform vec2 uResolution;

// How quickly the narrow beam falls off to either side of its line (-8 soft, -16 hard), the wide one that fills
// the gaps (-2 to -4), and a dot to the dot next to it (-2 soft, -4 hard).
uniform float uHardScan;
uniform float uHardBloom;
uniform float uHardPix;

// What the narrow beam comes to over the height of a line when it is 1 at its middle, and one over what the wide
// beam comes to: see CrtEffect.
uniform float uScanMean;
uniform float uBloomNorm;

// How much of the light the wide beam may carry. 1 keeps everything as bright as it was, less keeps more of the
// lines on what is white.
uniform float uMaxBloom;

// How much the glass bulges, sideways and upwards. 0 is flat, an eighth is a goldfish bowl.
uniform vec2 uWarp;

out vec4 FragColor;

vec2 warp(vec2 pos) {
  pos = pos * 2.0 - 1.0;
  pos *= 1.0 + pos.yx * pos.yx * uWarp;
  return pos * 0.5 + 0.5;
}

vec3 toSrgb(vec3 colour) {
  colour = max(colour, vec3(0.0));
  return mix(colour * 12.92, 1.055 * pow(colour, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), colour));
}

// What one scan line gives a pixel: the narrow beam driven up to where its brightest colour would clip, and what
// light that still leaves missing put into the wide one.
vec3 beam(vec3 colour, float narrow, float wide) {
  float brightest = max(max(colour.r, colour.g), max(colour.b, 1e-4));
  float drive = clamp((1.0 / brightest - 1.0) / (1.0 - uScanMean), 0.0, 1.0 / uScanMean);
  float fill = min(1.0 - drive * uScanMean, uMaxBloom);

  return colour * (drive * narrow + fill * wide);
}

void main() {
  vec2 pos = warp(texCoords) * uResolution;

  // The dot we are in, and how far its middle is from us: the dots to either side are one and two further.
  vec2 cell = floor(pos);
  vec2 away = 0.5 - (pos - cell);

  // How much of the dots of a line gets here. They share out what light there is, so they are brought to a sum
  // of one: over three dots for the lines above and below, over five for the one we are on.
  vec4 across = away.x + vec4(-2.0, -1.0, 1.0, 2.0);
  vec4 sides = exp2(uHardPix * across * across);
  float centre = exp2(uHardPix * away.x * away.x);

  float three = 1.0 / (sides.y + centre + sides.z);
  float five = 1.0 / (sides.x + sides.y + centre + sides.z + sides.w);

  // The two beams of the three nearest lines.
  vec3 down = away.y + vec3(-1.0, 0.0, 1.0);
  vec3 narrow = exp2(uHardScan * down * down);
  vec3 wide = exp2(uHardBloom * down * down) * uBloomNorm;

  vec2 texel = 1.0 / uResolution;
  vec2 middle = (cell + 0.5) * texel;

  // Three dots in two reads: one between the first and the middle one, one between the middle one and the last,
  // the middle one going half to each.
  vec2 halves = vec2(sides.y, sides.z) + centre * 0.5;
  vec2 halfAt = vec2(-1.0 + centre * 0.5 / halves.x, sides.z / halves.y) * texel.x;

  // Five dots in three: the outer two of either side in one read each, the middle one by itself.
  vec2 outer = sides.xz + sides.yw;
  vec2 outerAt = vec2(-2.0 + sides.y / outer.x, 1.0 + sides.w / outer.y) * texel.x;

  float above = middle.y - texel.y, below = middle.y + texel.y;

  vec3 lineA = (
    texture(uSource, vec2(middle.x + halfAt.x, above)).rgb * halves.x +
    texture(uSource, vec2(middle.x + halfAt.y, above)).rgb * halves.y) * three;

  vec3 lineB = (
    texture(uSource, vec2(middle.x + outerAt.x, middle.y)).rgb * outer.x +
    texture(uSource, middle).rgb * centre +
    texture(uSource, vec2(middle.x + outerAt.y, middle.y)).rgb * outer.y) * five;

  vec3 lineC = (
    texture(uSource, vec2(middle.x + halfAt.x, below)).rgb * halves.x +
    texture(uSource, vec2(middle.x + halfAt.y, below)).rgb * halves.y) * three;

  vec3 colour =
    beam(lineA, narrow.x, wide.x) +
    beam(lineB, narrow.y, wide.y) +
    beam(lineC, narrow.z, wide.z);

  FragColor = vec4(toSrgb(colour), 1.0);
}
