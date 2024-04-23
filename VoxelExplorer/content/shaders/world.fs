#version 460 core


layout(location = 0) in vec2 iTexCoords;
layout(location = 1) in float drawId;

out vec4 frag_out;

void main() {
    frag_out = vec4(vec3(drawId), 1.0);
}