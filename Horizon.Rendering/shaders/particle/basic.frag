#version 410 core

in float alive;
in vec2 fragPos;

// Albedo, surface and material are the attachments of a DeferredRenderer2D, see its summary for what goes where.
// Drawn straight to the window only the first of them goes anywhere.
layout(location = 0) out vec4 AlbedoColor;
layout(location = 1) out vec4 SurfaceColor;
layout(location = 2) out vec4 MaterialColor;

uniform vec3 uStartColor;
uniform vec3 uEndColor;

// How much of a particle shows no matter the light, at the start and at the end of its life.
uniform float uStartEmissive;
uniform float uEndEmissive;

void main() {
  if (alive <= 0.0) discard;
  AlbedoColor = vec4(mix(uEndColor * 2.0, uStartColor, alive), alive);

  // Particles fade out as they die, and so does what they say about the surface: a spark that is nearly gone
  // must not leave whatever is behind it glowing.
  SurfaceColor = vec4(0.5, 0.5, mix(uEndEmissive, uStartEmissive, alive), alive);

  // A particle isn't shiny, and takes the shine off of what is behind it as far as it covers it.
  MaterialColor = vec4(0.0, 0.0, 0.0, alive);
}
