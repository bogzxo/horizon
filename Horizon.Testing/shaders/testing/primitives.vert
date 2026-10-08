#version 460 core

// The star in the shapes example: a Mesh2D's triangles, spun round and breathing on the GPU.
// A Vertex2D is a position and a texture coordinate, and the star doesn't need a texture, so it uses the
// coordinate for its own stuff: x is how far out from the middle a vertex is (0 middle, 1 tip),
// y is 1 for the ring round the outside, which stays put while the star breathes.

#include <common/camera.glsl>

layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vTexCoords;
uniform vec2 uCentre;
uniform float uTime;

out float edge;

void main() {
  float angle = uTime * 0.6;
  float breathe = 1.0 + 0.1 * sin(uTime * 2.2) * vTexCoords.x * (1.0 - vTexCoords.y);
  vec2 turned = mat2(cos(angle), sin(angle), -sin(angle), cos(angle)) * vPos * breathe;

  edge = vTexCoords.x;
  gl_Position = uViewProjection * vec4(turned + uCentre, 0.0, 1.0);
}
