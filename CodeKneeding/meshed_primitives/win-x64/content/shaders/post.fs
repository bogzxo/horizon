#version 460 core

struct VertexData {
    vec2 texCoords;
    vec3 fragPos;
};

layout(location=0) in VertexData iVertexData;

out vec4 frag_out;

uniform sampler2D uTexAlbedo;

void main() {
    vec3 albedo = texture(uTexAlbedo, iVertexData.texCoords).rgb;
    
    frag_out = vec4(albedo, 1.0);
}
