#version 460 core

#include <common/gbuffer.glsl>

// Every shape is a signed distance field: how far a fragment is from the edge of the shape, negative inside. A filled
// shape is everything at a negative distance, an outline is everything within half the thickness of the edge, and
// the edge itself is smoothed over a pixel either way, whatever size the shape is drawn at. So a circle is a circle
// and not a polygon, and a line is as thin as it is asked to be.

layout(location = 0) in vec2 local;
layout(location = 1) in vec4 color;
layout(location = 2) flat in vec2 halfSize;
layout(location = 3) flat in float thickness;
layout(location = 4) flat in float rounding;
layout(location = 5) flat in uint kind;
layout(location = 6) flat in vec2 motion;

// How near the shapes are, from 0 (the backdrop) to 1 (right in front), and how much of them shows whatever the
// light is (debug drawings want to be seen in the dark: 1).
uniform float uNearness;
uniform float uEmissive;

const uint KIND_RECTANGLE = 0u;
const uint KIND_CIRCLE = 1u;
const uint KIND_SEGMENT = 2u;
const uint KIND_TRIANGLE = 3u;

float rectangle(vec2 at, vec2 extent, float round) {
  round = min(round, min(extent.x, extent.y));
  vec2 d = abs(at) - extent + vec2(round);
  return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - round;
}

float circle(vec2 at, float radius) {
  return length(at) - radius;
}

// A box with round caps: the segment itself lies along X, from -extent.x to extent.x, and the caps are the thickness
float capsule(vec2 at, vec2 extent) {
  at.x = max(abs(at.x) - extent.x, 0.0);
  return length(at) - extent.y;
}

// An isosceles triangle standing on its base, its tip at the top: the shape the old renderer drew, kept so a
// rotation of 0 still points up.
float triangle(vec2 at, vec2 extent) {
  // Its corners: the two at the bottom and the tip. Base at -extent.y, tip at extent.y
  vec2 a = vec2(-extent.x, -extent.y);
  vec2 b = vec2(extent.x, -extent.y);
  vec2 c = vec2(0.0, extent.y);

  vec2 e0 = b - a, e1 = c - b, e2 = a - c;
  vec2 v0 = at - a, v1 = at - b, v2 = at - c;

  vec2 pq0 = v0 - e0 * clamp(dot(v0, e0) / dot(e0, e0), 0.0, 1.0);
  vec2 pq1 = v1 - e1 * clamp(dot(v1, e1) / dot(e1, e1), 0.0, 1.0);
  vec2 pq2 = v2 - e2 * clamp(dot(v2, e2) / dot(e2, e2), 0.0, 1.0);

  float s = sign(e0.x * e2.y - e0.y * e2.x);
  vec2 d = min(min(vec2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
                   vec2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))),
                   vec2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
  return -sqrt(d.x) * sign(d.y);
}

void main() {
  float distance;
  if (kind == KIND_CIRCLE) distance = circle(local, halfSize.x);
  else if (kind == KIND_SEGMENT) distance = capsule(local, halfSize);
  else if (kind == KIND_TRIANGLE) distance = triangle(local, halfSize);
  else distance = rectangle(local, halfSize, rounding);

  // An outline is a band around the edge. A segment is a shape of its own, its thickness is its width
  if (thickness > 0.0 && kind != KIND_SEGMENT) distance = abs(distance) - thickness * 0.5;

  // Smoothed over about a pixel of the screen, however big the shape is on it
  float pixel = max(fwidth(distance), 0.00001);
  float coverage = clamp(0.5 - distance / pixel, 0.0, 1.0);

  vec4 result = vec4(color.rgb, color.a * coverage);
  if (result.a <= 0.0) discard;

  writeFlat(result, uEmissive, motion, uNearness);
}
