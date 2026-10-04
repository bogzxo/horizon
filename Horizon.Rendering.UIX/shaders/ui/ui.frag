#version 460 core

// Must match UISource.
const uint SOURCE_SKIN = 1u;
const uint SOURCE_FONT = 2u;
const uint SOURCE_IMAGE = 3u;

layout(location = 0) in vec2 texCoords;
layout(location = 1) in vec4 color;
layout(location = 2) flat in uint source;

layout(location = 0) out vec4 AlbedoColor;

uniform sampler2D uSkin;
uniform sampler2D uFont;
uniform sampler2D uImage;

void main() {
  // Sampled up front, outside of any branch, so the mip level is always well defined.
  vec4 skin = texture(uSkin, texCoords);
  float coverage = texture(uFont, texCoords).a;
  vec4 image = texture(uImage, texCoords);

  vec4 result = color;

  if (source == SOURCE_SKIN) result *= skin;
  else if (source == SOURCE_FONT) result.a *= coverage; // the atlas only says where the ink is
  else if (source == SOURCE_IMAGE) result *= image;

  if (result.a <= 0.0) discard;
  AlbedoColor = result;
}
