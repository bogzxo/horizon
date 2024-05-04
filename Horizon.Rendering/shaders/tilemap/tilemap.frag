#version 460 core

layout(location = 0) out vec4 AlbedoColor;
layout(location = 1) out uint StencilOutput;

layout(location = 0) flat in uint vIndex;
in vec2 texCoords;
in vec3 color;
in float shouldDiscard;
in vec2 fragPos;

uniform sampler2D uTextureAlbedo;
uniform sampler2D uTextureNormal;

uniform bool uWireframeEnabled;

void main() {
  AlbedoColor = texture(uTextureAlbedo, texCoords) * vec4(color, 1.0);

  if (shouldDiscard == 1.0 || AlbedoColor.a < 0.001)
    discard;
  
  StencilOutput = vIndex;
}
