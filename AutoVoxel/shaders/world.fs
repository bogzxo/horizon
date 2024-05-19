#version 460 core

layout(location = 0) in vec2 iTexCoords;
layout(location = 1) in vec3 iNormal;

uniform sampler2D uTexAlbedo;

out vec4 frag_out;

vec3 lightDir = vec3(0.3, 0.4, 0.5);

void main() {
    vec4 col = texture(uTexAlbedo, iTexCoords);
    col *= max(0.3, abs(dot(lightDir, iNormal)));
    frag_out = col;
}
