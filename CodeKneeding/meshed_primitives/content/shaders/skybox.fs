#version 460 core

layout(location=0) in vec3 iCamDir;

out vec4 frag_out;

uniform samplerCube uTexAlbedo;

void main() {
    frag_out = texture(uTexAlbedo, iCamDir);
}
