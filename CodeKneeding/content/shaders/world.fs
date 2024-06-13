#version 460 core

layout(early_fragment_tests) in;

layout(location = 0) in VS_OUT {
    vec2 texCoords; 
    vec3 fragPos;
    vec4 fragPosLightSpaceFar;
    vec4 fragPosLightSpaceNear;
    vec3 normal;
    vec3 tint;
    flat int cascade;
} fs_in;

uniform sampler2D uTexAlbedo;

uniform sampler2DShadow uTexSunDepthFar;
uniform sampler2DShadow uTexSunDepthNear;

layout(location = 0) out vec4 frag_out;

uniform vec3 uSunDir;

float calcShadow(float bias) {
    if (fs_in.cascade < 0) return 1.0;
    // perform perspective divide
    vec3 projCoords = fs_in.cascade == 0 ? (fs_in.fragPosLightSpaceNear.xyz / fs_in.fragPosLightSpaceNear.w) : (fs_in.fragPosLightSpaceFar.xyz / fs_in.fragPosLightSpaceFar.w);
    
    // transform to [0,1] range
    projCoords = projCoords * 0.5 + 0.5;
    projCoords.z -= max(0.00005 * (1.0 - bias), 0.000005) + 0.0005 * fs_in.cascade;
    // projCoords.z -= 0.000005 * fs_in.cascade * 10;
    
    // the depth texture has comparisons enabled on it, so this will simply return the result of how many samples passed the comparison (multi sample because linear) 
    float closestDepth = texture((fs_in.cascade == 0 ? uTexSunDepthNear : uTexSunDepthFar), projCoords).r;
    return mix(0.5, 1.0, closestDepth);
}

void main() {
    float bias = abs(dot(uSunDir, fs_in.normal));
    vec3 col = (texture(uTexAlbedo, fs_in.texCoords)).rgb * vec3(max(0.3, bias) + 0.2);
    frag_out = vec4(col * (calcShadow(bias)) * fs_in.tint , 1.0);
}
