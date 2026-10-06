#version 410 core

in float alive;
in vec2 fragPos;

// How fast the particle goes across the screen (halves of it a second), for whoever blurs motion
flat in vec2 motion;

// Albedo, surface, material and motion are the attachments of a DeferredRenderer2D, see its summary for what goes
// where. Drawn straight to the window only the first of them goes anywhere.
layout(location = 0) out vec4 AlbedoColor;
layout(location = 1) out vec4 SurfaceColor;
layout(location = 2) out vec4 MaterialColor;
layout(location = 3) out vec4 MotionColor;

uniform vec3 uStartColor;
uniform vec3 uEndColor;

// How much of a particle shows no matter the light, at the start and at the end of its life.
uniform float uStartEmissive;
uniform float uEndEmissive;

// How near the particles are: from 0 (the backdrop) to 1 (right in front).
uniform float uNearness;

// Motion goes into two bytes: halves of the screen a second, MOTION_RANGE of them either way, with 128 standing for
// none so that standing still is exact. Must match every other shader that writes or reads it.
const float MOTION_RANGE = 2.0;

vec2 encodeMotion(vec2 speed) {
  return (128.0 + clamp(speed / MOTION_RANGE, -1.0, 1.0) * 127.0) / 255.0;
}

void main() {
  if (alive <= 0.0) discard;
  AlbedoColor = vec4(mix(uEndColor * 2.0, uStartColor, alive), alive);

  // Particles fade out as they die, and so does what they say about the surface: a spark that is nearly gone
  // must not leave whatever is behind it glowing.
  SurfaceColor = vec4(0.5, 0.5, mix(uEndEmissive, uStartEmissive, alive), alive);

  // A particle isn't shiny, and takes the shine off of what is behind it as far as it covers it.
  MaterialColor = vec4(0.0, 0.0, 0.0, alive);
  MotionColor = vec4(encodeMotion(motion), uNearness, alive);
}
