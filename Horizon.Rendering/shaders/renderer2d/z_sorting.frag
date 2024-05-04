#version 410 core

layout(location = 0) in vec2 texCoords;

uniform sampler2D uBackgroundAlbedo;
uniform usampler2D uBackgroundZ;

uniform sampler2D uForegroundAlbedo;
uniform usampler2D uForegroundZ;

out vec4 FragColor;

uint readZ(usampler2D sampl) {
    return texelFetch(sampl, ivec2(texCoords * textureSize(sampl, 0)), 0).r;
}

void main() {
    vec4 background = texture(uBackgroundAlbedo, texCoords);
    vec4 foreground = texture(uForegroundAlbedo, texCoords);

    uint backZ = readZ(uBackgroundZ);
    uint foreZ = readZ(uForegroundZ);

    vec3 final = background.rgb;

    if (foreZ > backZ && foreground.a > 0.01) final = foreground.rgb;

    FragColor = vec4(final, 1.0);
}
