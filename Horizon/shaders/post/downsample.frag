#version 460 core

// A picture at a quarter of its size, every pixel the average of the four by four it stands for: four reads, each
// landing where four pixels meet so the GPU averages those. A plain read would take one pixel out of sixteen and
// leave the rest out, which shimmers on anything that moves.

layout(location = 0) in vec2 texCoords;

layout(binding = 0) uniform sampler2D uSource;

// The size of a pixel of the picture that is read, in texture coordinates
uniform vec2 uTexel;

out vec4 FragColor;

void main() {
  FragColor = 0.25 * (
    texture(uSource, texCoords + vec2(-uTexel.x, -uTexel.y)) +
    texture(uSource, texCoords + vec2( uTexel.x, -uTexel.y)) +
    texture(uSource, texCoords + vec2(-uTexel.x,  uTexel.y)) +
    texture(uSource, texCoords + vec2( uTexel.x,  uTexel.y)));
}
