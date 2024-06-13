#version 460 core

#include "post.h"

layout(location=0) in VertexData iVertexData;

out vec4 frag_out;

uniform sampler2D uTexAlbedo;
uniform sampler2D uTexDepth;
uniform sampler2D uTexSky;

#define near 0.1
#define far 500.0

float linearize_depth(float depth) {
    float z = depth * 2.0 - 1.0; // back to NDC 
    return (2.0 * near) / (far + near - z * (far - near));
}

void main() {
    float depth = (texture(uTexDepth, iVertexData.texCoords).r);
    vec3 albedo = texture(uTexAlbedo, iVertexData.texCoords).rgb;
    vec3 sky = texture(uTexSky, iVertexData.texCoords).rgb;
    
    frag_out = vec4((depth == 1.0 ? sky : albedo), 1.0);
}
