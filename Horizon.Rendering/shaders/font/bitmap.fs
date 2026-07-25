#version 460 core

layout(location = 0) in vec2 texCoords;

layout(location = 0) out vec4 AlbedoColor;

uniform sampler2D u_bitmap;

void main() {
  AlbedoColor = texture(u_bitmap, texCoords);
  if (AlbedoColor.w < 0.1) discard;
}