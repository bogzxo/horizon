#version 460 core

// The first pass of the motion blur: the picture cut into squares, and for each of them the fastest thing in it.
// This is what lets the blur find out cheaply that something fast is passing near a pixel that itself stands still.

layout(binding = 0) uniform sampler2D uMotion;

// How many pixels a square is wide and high. Must match MotionBlurEffect.TILE_SIZE.
const int TILE = 16;

// Every other pixel of a square is looked at, each way: a quarter of the reads, and what moves is rarely a pixel wide.
const int STEP = 2;

out vec4 FragColor;

void main() {
  ivec2 corner = ivec2(gl_FragCoord.xy) * TILE;
  ivec2 last = textureSize(uMotion, 0) - 1;

  // Kept the way it is written into the picture (see encodeMotion in the shaders that do), 128 being none
  vec2 fastest = vec2(128.0 / 255.0);
  float speed = 0.0;

  for (int y = 0; y < TILE; y += STEP) {
    for (int x = 0; x < TILE; x += STEP) {
      vec2 motion = texelFetch(uMotion, min(corner + ivec2(x, y), last), 0).rg;
      vec2 away = motion * 255.0 - 128.0;
      float found = dot(away, away);

      if (found > speed) {
        speed = found;
        fastest = motion;
      }
    }
  }

  FragColor = vec4(fastest, 0.0, 1.0);
}
