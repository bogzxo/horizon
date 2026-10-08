#version 460 core

// Puts the picture of a Renderer2D on screen as it is.

layout(location = 0) in vec2 texCoords;

layout(binding = 0) uniform sampler2D uTexAlbedo;

out vec4 FragColor;

void main() {
    FragColor = texture(uTexAlbedo, texCoords);
}
