#version 460 core

// The one pass of the motion blur that does the remembering: the new picture blended into what is left of the old ones.
// What is left of the old ones is read from where it was on screen back then, which is not where it is now if the camera
// has moved since. Read from there, a street that only went by because the camera did stays sharp and only what really
// moves through it leaves a trail.

layout(location = 0) in vec2 texCoords;

layout(binding = 0) uniform sampler2D uSource;
layout(binding = 1) uniform sampler2D uHistory;

// How much of the old picture is kept, 0 to 1.
uniform float uKeep;

// How far everything that stands still has moved across the picture since the last frame, in pictures (so 0.5 is half of it).
uniform vec2 uShift;

out vec4 FragColor;

void main() {
  vec4 now = texture(uSource, texCoords);
  vec2 before = texCoords + uShift;

  // What has only just come into view was never on screen, there is nothing to remember of it
  bool seen = all(greaterThanEqual(before, vec2(0.0))) && all(lessThanEqual(before, vec2(1.0)));

  FragColor = seen ? mix(now, texture(uHistory, before), uKeep) : now;
}
