#version 460 core

#include <common/gbuffer.glsl>

in float alive;
in vec2 fragPos;

// How fast the particle goes across the screen (halves of it a second), for whoever blurs motion
flat in vec2 motion;

uniform vec3 uStartColor;
uniform vec3 uEndColor;

// How much of a particle shows no matter the light, at the start and at the end of its life.
uniform float uStartEmissive;
uniform float uEndEmissive;

// How near the particles are: from 0 (the backdrop) to 1 (right in front).
uniform float uNearness;

void main() {
  if (alive <= 0.0) discard;

  // Particles fade out as they die, and so does what they say about the surface: a spark that is nearly gone
  // must not leave whatever is behind it glowing. A particle isn't shiny, and takes the shine off of what is
  // behind it as far as it covers it.
  writeFlat(vec4(mix(uEndColor * 2.0, uStartColor, alive), alive), mix(uEndEmissive, uStartEmissive, alive), motion, uNearness);
}
