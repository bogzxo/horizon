#version 460 core

layout(location = 2) out vec4 AlbedoColor;
layout(location = 3) out uint StencilColor;

layout(location = 0) in vec2 texCoords;
layout(location = 1) in vec2 fragPos;
layout(location = 2) flat in uint stencil;
layout(location = 3) in vec2 oRawTexCoords;

uniform sampler2D uTexture;
    
void main() {
	vec4 albedo = texture(uTexture, texCoords);
    if (albedo.a <= 0.01) discard;
    AlbedoColor = albedo;
    
    StencilColor = stencil;
}
 