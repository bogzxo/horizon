#version 460 core

#include <common/camera.glsl>

// A corner of the one quad every shape is drawn as, from -0.5 to 0.5.
layout(location = 0) in vec2 vPos;

// What the whole renderer is moved by.
uniform mat4 uModel;

// Must match ShapeInstance.cs (64 bytes). What the fields mean depends on the kind, see ShapeKind:
//   center  the middle of a rectangle, circle or triangle; one end of a segment
//   size    half the width and height of a rectangle or triangle; the radius (twice) of a circle; the other end of a segment
//   rotation in radians, anticlockwise around the middle. A segment has none
//   thickness 0 for a filled shape, the width of the line for an outline or a segment
//   rounding how far the corners of a rectangle are rounded
struct Shape {
  vec2 center;
  vec2 size;
  float rotation;
  float thickness;
  float rounding;
  float depth;
  uint color;
  uint kind;
  vec2 motion;
  vec2 padding0;
  vec2 padding1;
};

// Must match ShapeKind.
const uint KIND_RECTANGLE = 0u;
const uint KIND_CIRCLE = 1u;
const uint KIND_SEGMENT = 2u;
const uint KIND_TRIANGLE = 3u;
const uint KIND_MASK = 0xFFu;

// The shapes of this frame, bound as the range of the stream buffer the frame is in, see StreamBuffer.BindRange.
layout(std430, binding = 1) readonly restrict buffer ShapeData {
  Shape shapes[];
};

// Where the fragment is in the shape's own space (its middle at 0, unturned), what it is and how it is drawn.
layout(location = 0) out vec2 oLocal;
layout(location = 1) out vec4 oColor;
layout(location = 2) flat out vec2 oHalfSize;
layout(location = 3) flat out float oThickness;
layout(location = 4) flat out float oRounding;
layout(location = 5) flat out uint oKind;
layout(location = 6) flat out vec2 oMotion;

void main() {
  Shape shape = shapes[gl_BaseInstance + gl_InstanceID];
  uint kind = shape.kind & KIND_MASK;

  // The edge of a shape is smoothed over about a pixel, so the quad reaches a little past it: a pixel of the world
  // at the scale things are drawn at, which uMotionScale (halves of the screen a unit) and the viewport give
  float pixel = 2.0 / max(uViewportSize.x * uMotionScale.x, 0.0001);

  vec2 center = shape.center;
  vec2 halfSize = shape.size;
  float rotation = shape.rotation;

  if (kind == KIND_SEGMENT) {
    // A segment is a box as long as the line and as wide as it is thick, turned to lie along it, with round caps
    // that are the thickness over its ends
    vec2 along = shape.size - shape.center;
    center = (shape.center + shape.size) * 0.5;
    halfSize = vec2(length(along) * 0.5, shape.thickness * 0.5);
    rotation = atan(along.y, along.x);
  }

  // A line is drawn either side of the edge, so an outline reaches half its thickness out
  float reach = (kind == KIND_SEGMENT ? shape.thickness * 0.5 : (shape.thickness > 0.0 ? shape.thickness * 0.5 : 0.0)) + pixel * 1.5;
  vec2 extent = halfSize + vec2(reach);

  vec2 local = vPos * 2.0 * extent;
  float c = cos(rotation), s = sin(rotation);
  vec2 turned = vec2(local.x * c - local.y * s, local.x * s + local.y * c);

  vec4 world = uModel * vec4(center + turned, shape.depth, 1.0);
  gl_Position = uViewProjection * world;

  oLocal = local;
  oColor = unpackUnorm4x8(shape.color);
  oHalfSize = halfSize;
  oThickness = shape.thickness;
  oRounding = shape.rounding;
  oKind = kind;
  oMotion = screenMotion(mat2(uModel) * shape.motion);
}
