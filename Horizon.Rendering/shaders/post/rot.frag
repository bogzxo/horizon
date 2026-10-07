#version 410 core

// A picture rotting away, see RotTransition. Blotches of noise spread until they have eaten everything.
// A murky stain creeps ahead of each blotch and it has a sickly crust along its edge.
// It is drawn blended over the finished frame, so all this decides is the colour of the rot and how much of what is under it still shows.

layout(location = 0) in vec2 texCoords;

// How much of the picture is gone, from 0 to 1
uniform float uCover;

// What is left where the rot has been, and the colour of the crust along the edge of it
uniform vec3 uColor;
uniform vec3 uEdgeColor;

// How many blotches fit across and up the screen
uniform vec2 uCells;

// How many blocks the screen is cut into, which keeps the rot as chunky as pixel art
uniform vec2 uBlocks;

// Somewhere else in the noise every time, so no two transitions rot the same way
uniform float uSeed;

out vec4 FragColor;

// How wide the stain and the crust are, in the same units as the noise (0 to 1)
const float STAIN = 0.16;
const float CRUST = 0.05;

// Noise bunches up around the middle. Stretched by this much it is spread over all of 0 to 1,
// so the rot gets going the moment it starts instead of sitting there for a third of its time
const float CONTRAST = 1.7;

// A direction for every corner of the grid, the same one every time it is asked for
vec2 gradient(vec2 corner) {
  float angle = 6.2831853 * fract(sin(dot(corner, vec2(127.1, 311.7)) + uSeed) * 43758.5453);
  return vec2(cos(angle), sin(angle));
}

// Perlin noise, somewhere between -0.7 and 0.7
float perlin(vec2 point) {
  vec2 corner = floor(point);
  vec2 within = fract(point);
  vec2 fade = within * within * within * (within * (within * 6.0 - 15.0) + 10.0);

  float bottomLeft = dot(gradient(corner), within);
  float bottomRight = dot(gradient(corner + vec2(1.0, 0.0)), within - vec2(1.0, 0.0));
  float topLeft = dot(gradient(corner + vec2(0.0, 1.0)), within - vec2(0.0, 1.0));
  float topRight = dot(gradient(corner + vec2(1.0, 1.0)), within - vec2(1.0, 1.0));

  return mix(mix(bottomLeft, bottomRight, fade.x), mix(topLeft, topRight, fade.x), fade.y);
}

// A few layers of it on top of each other for ragged edges, squeezed into 0 to 1
float blotches(vec2 point) {
  float sum = 0.0;
  float weight = 0.5;
  float total = 0.0;

  for (int layer = 0; layer < 4; layer++) {
    sum += perlin(point) * weight;
    total += weight;
    point *= 2.03;
    weight *= 0.5;
  }

  return clamp(sum / total * CONTRAST + 0.5, 0.0, 1.0);
}

void main() {
  // Every block of the screen rots as one
  vec2 block = (floor(texCoords * uBlocks) + 0.5) / uBlocks;
  float noise = blotches(block * uCells);

  // The front moves through the noise as the cover goes up. It overshoots by the width of the stain and the crust,
  // so that at a cover of 1 even the last pixel to go is all the way gone
  float front = uCover * (1.0 + STAIN + CRUST);
  float behind = front - noise;

  // Not reached yet
  if (behind <= 0.0) {
    FragColor = vec4(0.0);
    return;
  }

  // First the stain, which is the rot showing through thinly. Then the crust, which hides what was there for good and darkens into the rot itself
  if (behind < STAIN) {
    float stain = behind / STAIN;
    FragColor = vec4(mix(uColor, uEdgeColor, 0.35), stain * stain * 0.75);
    return;
  }

  float crust = clamp((behind - STAIN) / CRUST, 0.0, 1.0);
  FragColor = vec4(mix(uEdgeColor, uColor, crust), 1.0);
}
