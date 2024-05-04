#version 410 core

in float alive;
in vec2 fragPos;

layout(location = 2) out vec4 AlbedoColor;
layout(location = 3) out uint stencilDepth;

uniform vec3 uStartColor;
uniform vec3 uEndColor;

void main() {
  if (alive <= 0.0) discard; 
  AlbedoColor = vec4(mix(uEndColor * 2.0, uStartColor, alive), alive);
  stencilDepth = 10;
}