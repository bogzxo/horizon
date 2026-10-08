#version 460 core

// Lights the G-buffer of a DeferredRenderer2D and puts the result on screen. The lights, what blocks them and the
// shadows live in lighting/direct.glsl, shared with the path traced lighting, which hands in what it found through
// uTexGi when it is on.

#include <lighting/direct.glsl>

layout(location = 0) in vec2 texCoords;

// rgb what was drawn, unlit.
layout(binding = 0) uniform sampler2D uTexAlbedo;

// rg the normal (0.5 being none), b how emissive it is.
layout(binding = 1) uniform sampler2D uTexSurface;

// r how shiny it is.
layout(binding = 2) uniform sampler2D uTexMaterial;

// What the path traced lighting found, see PathTracedLighting2D. Only read when uLightingMode is 1.
layout(binding = 4) uniform sampler2D uTexGi;

uniform int uLightingMode;
uniform float uGiStrength;
uniform float uGiAmbientScale;

uniform vec3 uAmbient;

// The size of the squares of the world that are lit as a whole, 0 for every pixel by itself.
uniform float uPixelSize;

out vec4 FragColor;

// Leaves a colour as it is up to here, and rolls whatever is over off towards 1: lights can be a lot brighter
// than white without everything they touch turning into a flat patch. It is the colour as a whole that is brought
// down, by its brightest channel, so what is lit too brightly keeps the colour of the light instead of going white.
vec3 rollOff(vec3 color) {
  const float knee = 0.8;

  float brightest = max(color.r, max(color.g, color.b));
  if (brightest <= knee) return color;

  float rolled = knee + (1.0 - knee) * (1.0 - exp(-(brightest - knee) / (1.0 - knee)));
  return color * (rolled / brightest);
}

void main() {
  vec3 albedo = texture(uTexAlbedo, texCoords).rgb;
  vec4 surface = texture(uTexSurface, texCoords);
  float emissive = surface.b;
  float shine = texture(uTexMaterial, texCoords).r * uSpecularIntensity;

  // Where on screen we are is where in the world we are, as far as the camera goes.
  vec2 position = (uInverseViewProjection * vec4(texCoords * 2.0 - 1.0, 0.0, 1.0)).xy;

  // Pixel art is lit a pixel of the art at a time, from the middle of it.
  if (uPixelSize > 0.0) position = (floor(position / uPixelSize) + 0.5) * uPixelSize;

  bool hasNormal;
  vec3 normal = surfaceNormal(surface, hasNormal);

  vec3 highlights, glow;
  vec3 light = directLight(position, normal, hasNormal, shine, highlights, glow);

  // For looking at what the tracer found on its own, see DeferredRenderer2D.ShowTracedLight
  if (uLightingMode == 2) {
    FragColor = vec4(texture(uTexGi, texCoords).rgb, 1.0);
    return;
  }

  if (uLightingMode == 1) {
    // Bounced light and what the lights throw as things with a size, gathered by the path tracer. The ambient makes
    // way for it, the dark corners get their light round the corner now instead of out of nowhere
    light += uAmbient * uGiAmbientScale + texture(uTexGi, texCoords).rgb * uGiStrength;
  } else {
    light += uAmbient;
  }

  // What is emissive shows as it was drawn no matter the light.
  vec3 color = albedo * mix(light, vec3(1.0), emissive) + highlights + glow;

  FragColor = vec4(rollOff(color), 1.0);
}
