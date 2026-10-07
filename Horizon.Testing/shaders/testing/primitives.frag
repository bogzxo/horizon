#version 460 core

// White hot in the middle, out to a colour that wanders round the rainbow at the tips (and the ring).

in float edge;

uniform float uTime;

out vec4 FragColor;

void main() {
  vec3 middle = vec3(1.0, 0.97, 0.78);
  vec3 tips = 0.55 + 0.45 * cos(uTime * 0.7 + vec3(0.0, 2.1, 4.2));
  FragColor = vec4(mix(middle, tips, smoothstep(0.1, 0.9, edge)), 1.0);
}
