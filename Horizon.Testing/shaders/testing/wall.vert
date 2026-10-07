#version 460 core

// The brick wall of the lighting test: a rectangle over the whole view, which works out what of the wall
// every one of its pixels is looking at from where in the world it is.

layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vTexCoords;

uniform mat4 uInverseViewProjection;

out vec2 worldPos;

void main() {
  worldPos = (uInverseViewProjection * vec4(vPos, 0.0, 1.0)).xy;
  gl_Position = vec4(vPos, 0.0, 1.0);
}
