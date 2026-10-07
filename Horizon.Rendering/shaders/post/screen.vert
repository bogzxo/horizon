#version 410 core

// The vertex shader of every post processing pass: one triangle over everything, see PostProcessor.DrawScreen.

layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vTexCoords;

layout(location = 0) out vec2 texCoords;

void main() {
  texCoords = vTexCoords;
  gl_Position = vec4(vPos, 0.0, 1.0);
}
