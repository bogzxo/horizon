#version 460 core

layout(location = 0) in vec2 iTexCoords;
layout(location = 2) in vec3 iNormal;
layout(location = 1) in float iDrawId;

uniform sampler2D uTexture;

out vec4 frag_out;

vec3 lightDir = vec3(0.3, 0.4, 0.5);

void main() {
    vec4 col = texture(uTexture, iTexCoords);
    col *= max(0.3, dot(lightDir, iNormal));
    frag_out = col;
}