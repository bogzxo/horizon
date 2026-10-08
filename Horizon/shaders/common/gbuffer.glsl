// The outputs of everything that is drawn into a Renderer2D, see DeferredRenderer2D for what goes where. Drawn
// straight to the window (or into a renderer that doesn't light) only the first of them goes anywhere.
//
//   AlbedoColor   what was drawn, unlit
//   SurfaceColor  rg the normal (0.5 being none), b how emissive it is
//   MaterialColor r how shiny it is
//   MotionColor   rg how fast it goes across the screen (see motion.glsl), b how near it is (0 the backdrop, 1 right in front)
//
// The alpha of every one is how much of the pixel the fragment covers, they are all blended alike.

#include <common/motion.glsl>

layout(location = 0) out vec4 AlbedoColor;
layout(location = 1) out vec4 SurfaceColor;
layout(location = 2) out vec4 MaterialColor;
layout(location = 3) out vec4 MotionColor;

// Writes a fragment that has no surface to speak of (flat, not shiny) with how fast it goes and how near it is.
void writeFlat(vec4 albedo, float emissive, vec2 motion, float nearness) {
  AlbedoColor = albedo;
  SurfaceColor = vec4(0.5, 0.5, emissive, albedo.a);
  MaterialColor = vec4(0.0, 0.0, 0.0, albedo.a);
  MotionColor = vec4(encodeMotion(motion), nearness, albedo.a);
}
