#version 460 core

// Must match UIVertex.
layout(location = 0) in vec2 vPosition;
layout(location = 1) in vec2 vTexCoords;
layout(location = 2) in uint vColor;
layout(location = 3) in uint vSource;

uniform mat4 uViewProjection;

layout(location = 0) out vec2 oTexCoords;
layout(location = 1) out vec4 oColor;
layout(location = 2) flat out uint oSource;

void main() {
  oTexCoords = vTexCoords;
  oColor = unpackUnorm4x8(vColor);
  oSource = vSource;

  gl_Position = uViewProjection * vec4(vPosition, 0.0, 1.0);
}
