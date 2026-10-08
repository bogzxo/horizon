#version 460 core

// A flat colour over everything, for covering a scene up while it is swapped for another one. See FadeTransition.
// How much of what is under it still shows is up to the alpha of the colour, this is drawn blended.

uniform vec4 uColor;

out vec4 FragColor;

void main() {
  FragColor = uColor;
}
