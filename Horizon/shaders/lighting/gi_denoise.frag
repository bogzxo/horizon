#version 460 core

// Smooths what the tracer found without smearing it across the edge of a wall, a small blur that only takes in
// texels of the same kind (wall or air) as its own.

layout(location = 0) in vec2 texCoords;

layout(binding = 0) uniform sampler2D uTexGi;
layout(binding = 1) uniform sampler2D uTexDistance;

uniform vec2 uSize;
uniform float uRadius;  // texels, 0 for none

out vec4 FragColor;

void main() {
  vec2 texel = 1.0 / uSize;
  float kind = texture(uTexDistance, texCoords).g;

  if (uRadius <= 0.0) {
    FragColor = texture(uTexGi, texCoords);
    return;
  }

  vec3 total = vec3(0.0);
  float weights = 0.0;

  for (int y = -2; y <= 2; y++) {
    for (int x = -2; x <= 2; x++) {
      vec2 offset = vec2(x, y) * uRadius * 0.5 * texel;
      vec2 at = texCoords + offset;
      if (texture(uTexDistance, at).g != kind) continue;

      float w = exp(-float(x * x + y * y) * 0.25);
      total += texture(uTexGi, at).rgb * w;
      weights += w;
    }
  }

  FragColor = vec4(weights > 0.0 ? total / weights : texture(uTexGi, texCoords).rgb, 1.0);
}
