#version 410 core

layout(location = 0) out vec4 AlbedoColor;

in vec3 colour;

void main() {
  AlbedoColor = vec4(colour, 1.0);
}