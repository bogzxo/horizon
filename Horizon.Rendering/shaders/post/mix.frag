#version 410 core

// Two pictures blended into one. At an amount of 0 it is all the first one, at 1 all the other.
// This is what lets a blur come on without a pop, and what crossfades one scene into the next.

layout(location = 0) in vec2 texCoords;

uniform sampler2D uSource;
uniform sampler2D uOther;
uniform float uAmount;

out vec4 FragColor;

void main() {
  FragColor = mix(texture(uSource, texCoords), texture(uOther, texCoords), uAmount);
}
