#version 460 core

#include <common/gbuffer.glsl>

in vec2 texCoords;
flat in int flip;

// The image of the tile set, its normal map and its specular map, on the first three units.
layout(binding = 0) uniform sampler2D uTextureAlbedo;
layout(binding = 1) uniform sampler2D uTextureNormal;
layout(binding = 2) uniform sampler2D uTextureSpecular;

// Not every image comes with a normal map, or with a specular one.
uniform bool uHasNormal;
uniform bool uHasSpecular;

// What the layer multiplies everything in it by: its tint, and in the alpha how much of it there is.
uniform vec4 uTint;

// How much of the layer shows no matter the light (a sky, a glowing sign).
uniform float uEmissive;

// How fast the layer goes across the screen (halves of it a second), which for one that scrolls at a speed of its
// own is not how fast the map does, and how near it is: from 0 (the backdrop) to 1 (right in front).
uniform vec2 uMotion;
uniform float uNearness;

const int FLIP_HORIZONTAL = 1;
const int FLIP_VERTICAL = 2;
const int FLIP_DIAGONAL = 4;

void main() {
  vec4 albedo = texture(uTextureAlbedo, texCoords) * uTint;
  if (albedo.a <= 0.0)
    discard;

  AlbedoColor = albedo;

  // The way a surface faces turns over along with the tile it is on, or a tile that is mirrored would be lit from
  // the wrong side. The normal map has Y going up and the image has it going down, which is where the minus of the
  // diagonal comes from.
  vec2 normal = uHasNormal ? texture(uTextureNormal, texCoords).xy : vec2(0.5);
  if ((flip & FLIP_DIAGONAL) != 0) normal = vec2(1.0) - normal.yx;
  if ((flip & FLIP_HORIZONTAL) != 0) normal.x = 1.0 - normal.x;
  if ((flip & FLIP_VERTICAL) != 0) normal.y = 1.0 - normal.y;

  SurfaceColor = vec4(normal, uEmissive, albedo.a);

  float shine = uHasSpecular ? texture(uTextureSpecular, texCoords).r : 0.0;
  MaterialColor = vec4(shine, 0.0, 0.0, albedo.a);
  MotionColor = vec4(encodeMotion(uMotion), uNearness, albedo.a);
}
