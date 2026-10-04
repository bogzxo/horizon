#version 410 core

layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vOffset;
layout(location = 2) in float vAlive;

uniform mat4 uCameraView;
uniform mat4 uCameraProjection;

uniform mat4 uModel;

out float alive;
out vec2 fragPos;

void main() {
  alive = vAlive;
  fragPos = vPos + vOffset;

  // The compute simulator keeps dead particles in its pool, so they still arrive here as instances:
  // push them outside the clip volume so they never reach the rasteriser.
  gl_Position = vAlive > 0.0
    ? uCameraProjection * uCameraView * vec4(fragPos, 0.0, 1.0)
    : vec4(2.0, 2.0, 2.0, 1.0);
}