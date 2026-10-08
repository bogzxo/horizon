#version 460 core

// The brick wall of the lighting test: one triangle over the whole view (see FullScreenPass, there are no vertices,
// the corners come out of the vertex number), which works out what of the wall every one of its pixels is looking
// at from where in the world it is.

#include <common/camera.glsl>

out vec2 worldPos;

void main() {
  vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0;
  worldPos = (uInverseViewProjection * vec4(corner, 0.0, 1.0)).xy;
  gl_Position = vec4(corner, 0.0, 1.0);
}
