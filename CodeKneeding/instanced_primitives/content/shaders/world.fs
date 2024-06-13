#version 460 core

layout(location = 0) in VS_OUT {
    vec2 texCoords; 
    vec3 fragPos;
    vec4 fragPosLightSpaceFar;
    vec4 fragPosLightSpaceNear;
    vec3 normal;
    flat int cascade;
} fs_in;

uniform sampler2D uTexAlbedo;

uniform sampler2DShadow uTexSunDepthFar;
uniform sampler2DShadow uTexSunDepthNear;

layout(location = 0) out vec4 frag_out;

uniform vec3 uSunDir;

float calcShadow() {
    // perform perspective divide
    vec3 projCoords = fs_in.cascade == 0 ? (fs_in.fragPosLightSpaceNear.xyz / fs_in.fragPosLightSpaceNear.w) : (fs_in.fragPosLightSpaceFar.xyz / fs_in.fragPosLightSpaceFar.w);
    
    // transform to [0,1] range
    projCoords = projCoords * 0.5 + 0.5;
    
    // the depth texture has comparisons enabled on it, so this will simply return the result of how many samples passed the comparison (multi sample because linear) 
    float closestDepth = texture((fs_in.cascade == 0 ? uTexSunDepthNear : uTexSunDepthFar), projCoords).r;
    return mix(0.5, 1.0, closestDepth);
}

void main() {
    vec3 col = (texture(uTexAlbedo, fs_in.texCoords)).rgb * vec3(max(0.3, abs(dot(uSunDir, fs_in.normal))) + 0.2);
    frag_out = vec4(col * (calcShadow()), 1.0);
}
