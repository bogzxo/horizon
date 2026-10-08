#version 460 core

// One pass of the jump flood that finds, for every texel, the nearest texel that stops rays (rg) and the nearest
// that doesn't (ba), as texture coordinates. With a step of 0 it seeds from the scene, after that it floods with
// the step halving every pass, log2(size) of them, and every texel ends up knowing both. -1 means nothing found.

layout(location = 0) in vec2 texCoords;

layout(binding = 0) uniform sampler2D uTexScene;
layout(binding = 1) uniform sampler2D uTexFlood;

uniform int uStep;
uniform vec2 uSize;

out vec4 FragColor;

void main() {
  vec2 texel = 1.0 / uSize;

  if (uStep == 0) {
    bool blocker = texture(uTexScene, texCoords).a > 0.5;
    FragColor = vec4(blocker ? texCoords : vec2(-1.0), blocker ? vec2(-1.0) : texCoords);
    return;
  }

  vec2 bestBlocker = vec2(-1.0), bestFree = vec2(-1.0);
  float blockerDistance = 1e30, freeDistance = 1e30;

  for (int y = -1; y <= 1; y++) {
    for (int x = -1; x <= 1; x++) {
      vec2 at = texCoords + vec2(x, y) * float(uStep) * texel;
      if (any(lessThan(at, vec2(0.0))) || any(greaterThan(at, vec2(1.0)))) continue;

      vec4 seeds = texture(uTexFlood, at);

      if (seeds.x >= 0.0) {
        float d = distance(seeds.xy * uSize, texCoords * uSize);
        if (d < blockerDistance) { blockerDistance = d; bestBlocker = seeds.xy; }
      }
      if (seeds.z >= 0.0) {
        float d = distance(seeds.zw * uSize, texCoords * uSize);
        if (d < freeDistance) { freeDistance = d; bestFree = seeds.zw; }
      }
    }
  }

  FragColor = vec4(bestBlocker, bestFree);
}
