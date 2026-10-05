#version 460 core

// A brick wall made up on the spot, normal and specular maps included: there is nothing in the engine's own assets
// with either to test the lighting against. Writes all of the attachments of a DeferredRenderer2D, see its summary
// for what goes where.

in vec2 worldPos;

layout(location = 0) out vec4 AlbedoColor;
layout(location = 1) out vec4 SurfaceColor;
layout(location = 2) out vec4 MaterialColor;

// Without them the wall is as flat as anything that came without a normal map.
uniform bool uNormals;

// Without it none of the wall is shiny.
uniform bool uSpecular;

const vec2 BRICK_SIZE = vec2(48.0, 24.0);
const float MORTAR = 1.5; // how wide the gap between two bricks is, on either side of it
const float BEVEL = 6.0;  // how far in from its edge a brick slopes down

void main() {
  vec2 p = worldPos / BRICK_SIZE;

  // Every other row is shifted by half a brick.
  p.x += mod(floor(p.y), 2.0) * 0.5;

  vec2 brick = floor(p);
  vec2 within = fract(p);

  // No two bricks are quite the same colour.
  float tint = fract(sin(dot(brick, vec2(12.9898, 78.233))) * 43758.5453);
  vec3 colour = mix(vec3(0.42, 0.3, 0.27), vec3(0.58, 0.44, 0.37), tint);

  // How far it is to the nearest edge of the brick on each axis, in world units.
  vec2 edge = min(within, 1.0 - within) * BRICK_SIZE;
  bool mortar = min(edge.x, edge.y) < MORTAR;

  // One brick in four is glazed, the rest are as dull as the mortar.
  float shine = 0.0;

  vec2 normal = vec2(0.0);
  if (mortar) {
    colour = vec3(0.2, 0.19, 0.2);
  } else {
    if (tint > 0.75) {
      colour = vec3(0.2, 0.32, 0.42);
      shine = 0.9;
    }

    // Towards its edges a brick slopes away from its middle, which is what catches the light.
    vec2 away = sign(within - 0.5);
    vec2 slope = clamp(1.0 - (edge - MORTAR) / BEVEL, 0.0, 1.0);
    normal = away * slope * 0.7;
  }

  AlbedoColor = vec4(colour, 1.0);
  SurfaceColor = vec4(uNormals ? normal * 0.5 + 0.5 : vec2(0.5), 0.0, 1.0);
  MaterialColor = vec4(uSpecular ? shine : 0.0, 0.0, 0.0, 1.0);
}
