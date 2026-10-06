#version 410 core

// The first pass of the CRT effect: the picture at the size the tube shows it at, one pixel for every dot of a
// scan line. It is drawn into a texture that hands its colours back in linear light (see PostTarget.LinearLight),
// which is where the second pass does its sums.

layout(location = 0) in vec2 texCoords;

uniform sampler2D uSource;

// How far from the middle of a pixel of the result the four reads are, in the picture's own coordinates. Each
// read is blended between the pixels around it, so between the four of them they cover the whole of what one
// pixel of the result stands for: nothing is skipped over when the picture is a lot bigger than the tube.
uniform vec2 uSpread;

out vec4 FragColor;

void main() {
  FragColor = 0.25 * (
    texture(uSource, texCoords + uSpread * vec2(-1.0, -1.0)) +
    texture(uSource, texCoords + uSpread * vec2( 1.0, -1.0)) +
    texture(uSource, texCoords + uSpread * vec2(-1.0,  1.0)) +
    texture(uSource, texCoords + uSpread * vec2( 1.0,  1.0)));
}
