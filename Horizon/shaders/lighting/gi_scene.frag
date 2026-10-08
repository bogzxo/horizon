#version 460 core

// The scene as the path traced lighting sees it, at its own (smaller) size. Every texel says what radiance leaves
// it and whether it stops a ray. What stops rays is what blocks the lights (the occlusion map), and a light itself.
// What leaves a texel is a light's own light (a disc the size of the light), what an emissive surface glows with,
// and what a lit wall throws back, its colour times the light falling on it (straight from the lights, and what the
// path tracing found last frame, which is how light goes round more than one corner).

#include <lighting/direct.glsl>

layout(location = 0) in vec2 texCoords;

layout(binding = 0) uniform sampler2D uTexAlbedo;
layout(binding = 1) uniform sampler2D uTexSurface;
layout(binding = 4) uniform sampler2D uTexGiPrevious;

// How much of what falls on a wall it throws back, how big a light is as something rays can hit (world units, at
// the least) and how much brighter than its intensity it is as a thing to hit.
uniform float uBounce;
uniform float uEmitterRadius;
uniform float uEmitterBoost;
uniform float uPixelSize;

out vec4 FragColor;

void main() {
  vec2 position = (uInverseViewProjection * vec4(texCoords * 2.0 - 1.0, 0.0, 1.0)).xy;
  if (uPixelSize > 0.0) position = (floor(position / uPixelSize) + 0.5) * uPixelSize;

  vec3 albedo = texture(uTexAlbedo, texCoords).rgb;
  vec4 surface = texture(uTexSurface, texCoords);
  float emissive = surface.b;

  bool blocker = isSolid(cellOf(position));
  vec3 radiance = vec3(0.0);
  bool lamp = false;

  for (int i = 0; i < uLightCount; i++) {
    float reach = max(lights[i].size, uEmitterRadius);
    vec2 toLight = lights[i].position - position;
    if (dot(toLight, toLight) < reach * reach) {
      radiance += lights[i].color * (lights[i].intensity * uEmitterBoost);
      lamp = true;
    }
  }

  if (lamp) {
    FragColor = vec4(radiance, 1.0);
    return;
  }

  if (blocker) {
    // A wall glows with whatever it is emissive, and throws back the light that falls on it
    bool hasNormal;
    vec3 normal = surfaceNormal(surface, hasNormal);
    vec3 highlights, glow;
    vec3 direct = directLight(position, normal, hasNormal, 0.0, highlights, glow);
    vec3 before = texture(uTexGiPrevious, texCoords).rgb;

    radiance = albedo * emissive + albedo * (direct + before) * uBounce;
  }

  FragColor = vec4(radiance, blocker ? 1.0 : 0.0);
}
