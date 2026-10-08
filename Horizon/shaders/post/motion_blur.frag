#version 460 core

// The last pass of the motion blur: every pixel is smeared along the way things are moving around it, by as much
// as they move while the shutter is open.
//
// After "A Reconstruction Filter for Plausible Motion Blur" (McGuire, Hennessy, Bukowski and Osman, 2012), which is
// how games have done it since. A pixel doesn't blur by its own speed alone: something fast that passes next to it
// has to smear over it, or a moving thing would be a blur with a sharp outline. So the pixel looks along the way the
// fastest thing near it is going (which the two passes before this one worked out), and takes from every spot it
// looks at as much as that spot would really have left on it:
//
//   - what is nearer than the pixel and fast enough to reach it smears over it,
//   - what is behind the pixel shows through as far as the pixel itself is smeared thin,
//   - and two things that move together blur into each other.
//
// What is behind something that stands still never smears over it. That is what keeps a fighter the camera follows
// sharp while the whole street behind them goes by in a blur.
// The paper tells near from far by depth, which a flat picture doesn't have. Here everything that is drawn says how
// near it is instead (see DeferredRenderer2D), which for layers of sprites comes to the same thing.

layout(location = 0) in vec2 texCoords;

// The picture, and what is in it: rg how fast (see decode below), b how near.
// The alpha of the picture is smeared like its colours, which come multiplied by it: a picture that is see-through
// where there is nothing (a UI on a layer of its own) stays that, and what moves in it trails off into nothing.
layout(binding = 0) uniform sampler2D uSource;
layout(binding = 1) uniform sampler2D uMotion;

// The fastest motion around every square of the picture, see motion_tiles.frag and motion_spread.frag.
layout(binding = 2) uniform sampler2D uSpread;

// The size of the picture in pixels.
uniform vec2 uSize;

// Turns motion as it is written into how many pixels a thing is smeared to either side while the shutter is open.
uniform vec2 uReach;

// The furthest anything is smeared to either side, in pixels. Must not be more than the squares are wide.
uniform float uMaxReach;

out vec4 FragColor;

// How many spots along the way are looked at. Where exactly is shifted from pixel to pixel, which turns the steps
// between them into a fine grain rather than ghosts of the picture.
const int SAMPLES = 11;

// How much nearer something has to be to count as wholly in front.
const float NEAR_STEP = 0.05;

// Slower than this (in pixels of smear) is standing still.
const float STILL = 0.5;

vec2 decode(vec2 motion) {
  vec2 reach = (motion * 255.0 - 128.0) / 127.0 * uReach;
  float squared = dot(reach, reach);

  return squared > uMaxReach * uMaxReach ? reach * (uMaxReach * inversesqrt(squared)) : reach;
}

// 1 where a is at least as near as b, falling to 0 where it is clearly behind.
float nearer(float a, float b) {
  return clamp(1.0 + (a - b) / NEAR_STEP, 0.0, 1.0);
}

// How much of something that is smeared over a reach gets as far as some way off: all of it right there, none at the end.
float cone(float span, float reach) {
  return clamp(1.0 - span / max(reach, 0.0001), 0.0, 1.0);
}

// Whether somewhere is within a reach at all, with a soft edge.
float cylinder(float span, float reach) {
  return 1.0 - smoothstep(0.95 * reach, 1.05 * reach, span);
}

// Interleaved gradient noise (Jimenez): as good as random from one pixel to the next, without a texture.
float grain(vec2 pixel) {
  return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

void main() {
  vec4 colour = texture(uSource, texCoords);

  // Nothing near here is going anywhere: the picture as it is, for the price of two reads
  vec2 way = decode(texture(uSpread, texCoords).rg);
  float wayLength = length(way);

  if (wayLength < STILL) {
    FragColor = colour;
    return;
  }

  vec4 here = texture(uMotion, texCoords);
  float reach = max(length(decode(here.rg)), STILL);

  // The pixel itself, which counts for less the thinner it is smeared
  float weight = 1.0 / reach;
  vec4 sum = colour * weight;

  float shift = grain(gl_FragCoord.xy) - 0.5;

  for (int i = 0; i < SAMPLES; i++) {
    // From one end of the smear to the other, the middle (which is the pixel itself) left out
    float along = mix(-1.0, 1.0, (float(i) + shift + 1.0) / float(SAMPLES + 1));
    if (abs(along) < 0.04) continue;

    vec2 offset = way * along;
    vec2 at = texCoords + offset / uSize;

    vec4 there = texture(uMotion, at);
    float thereReach = max(length(decode(there.rg)), STILL);
    float span = length(offset);

    float covers =
      nearer(there.b, here.b) * cone(span, thereReach) +
      nearer(here.b, there.b) * cone(span, reach) +
      cylinder(span, thereReach) * cylinder(span, reach) * 2.0;

    weight += covers;
    sum += texture(uSource, at) * covers;
  }

  FragColor = sum / weight;
}
