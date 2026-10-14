#version 410 core

// The picture as it is, for an effect that has nothing to do to it this frame.

layout(location = 0) in vec2 texCoords;

uniform sampler2D uSource;

out vec4 FragColor;

void main() {
  FragColor = texture(uSource, texCoords);
}
