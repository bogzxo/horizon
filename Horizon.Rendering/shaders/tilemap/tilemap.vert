#version 460 core

layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vTexCoords;

layout(location = 2) in vec2 iPos;
layout(location = 3) in vec2 iTexCoords;
layout(location = 4) in vec3 iColor;

uniform mat4 uCameraView;
uniform mat4 uCameraProjection;
uniform uint uIndex;

layout(location = 0) flat out uint vIndex;
out vec2 texCoords;
out vec3 color;
out float shouldDiscard;
out vec2 fragPos;

void main() {
  vec4 worldPos = vec4(vPos + iPos, 0.0, 1.0);
  fragPos = vPos + iPos;
  vIndex = uIndex;
  texCoords = vTexCoords + iTexCoords;
  color = iColor;
  gl_Position = uCameraProjection * uCameraView * worldPos;
}
