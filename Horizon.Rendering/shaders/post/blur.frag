#version 410 core

// One pass of a gaussian blur, along whichever way uStep points. Run it twice (sideways, then up and down) for a blur in every direction.
// Nine taps of the bell curve for the price of five reads, by reading between two pixels and letting the GPU blend them.

layout(location = 0) in vec2 texCoords;

uniform sampler2D uSource;

// How far apart the reads are, in texture coordinates. Zero in one of the two is what makes this one way only
uniform vec2 uStep;

out vec4 FragColor;

void main() {
  vec4 sum = texture(uSource, texCoords) * 0.2270270270;

  sum += texture(uSource, texCoords + uStep * 1.3846153846) * 0.3162162162;
  sum += texture(uSource, texCoords - uStep * 1.3846153846) * 0.3162162162;
  sum += texture(uSource, texCoords + uStep * 3.2307692308) * 0.0702702703;
  sum += texture(uSource, texCoords - uStep * 3.2307692308) * 0.0702702703;

  // The alpha is blurred along with the colours, so this works on a see-through layer as well
  FragColor = sum;
}
