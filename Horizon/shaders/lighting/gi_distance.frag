#version 460 core

// What the flood found, turned into what the tracer wants. r is how far (in texels) the nearest thing that stops a
// ray is, g whether this texel stops them itself, ba where the nearest texel that doesn't is (texture coordinates).

layout(location = 0) in vec2 texCoords;

layout(binding = 0) uniform sampler2D uTexScene;
layout(binding = 1) uniform sampler2D uTexFlood;

uniform vec2 uSize;

out vec4 FragColor;

void main() {
  vec4 seeds = texture(uTexFlood, texCoords);
  bool blocker = texture(uTexScene, texCoords).a > 0.5;

  float d = seeds.x >= 0.0 ? distance(seeds.xy * uSize, texCoords * uSize) : 1e4;
  FragColor = vec4(blocker ? 0.0 : d, blocker ? 1.0 : 0.0, seeds.z >= 0.0 ? seeds.zw : texCoords);
}
