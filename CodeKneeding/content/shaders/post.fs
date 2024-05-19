#version 460 core

struct VertexData {
	vec2 texCoords;
	vec3 fragPos;
};

layout(location=0) in VertexData iVertexData;

uniform sampler2D uTexAlbedo;
uniform isampler2D uTexSun;
uniform sampler2D uTexDepth;
uniform sampler2D uTexFrag;

out vec4 frag_out;

#define zNear 0.01
#define zFar 100.0 

float depthToLinear(float depthValue) {
    return (2.0 * zNear) / (zFar + zNear - depthValue * (zFar - zNear));
}

float calcShadow(ivec4 fragPosLightSpace)
{
    // perform perspective divide
    ivec3 projCoords = fragPosLightSpace.xyz / fragPosLightSpace.w;
    // transform to [0,1] range
    projCoords = projCoords;
    // get closest depth value from light's perspective (using [0,1] range fragPosLight as coords)
    float closestDepth = texture(uTexDepth, iVertexData.texCoords).r; 
    // get depth of current fragment from light's perspective
    float currentDepth = projCoords.z;
    // check whether current frag pos is in shadow
    float shadow = currentDepth > closestDepth  ? 1.0 : 0.0;

    return shadow;
}  

void main() {
    ivec4 sunFrag = texture(uTexSun, iVertexData.texCoords);
    vec3 fragPos = texture(uTexFrag, iVertexData.texCoords).rgb;

    float shadow = calcShadow(sunFrag);
    
    float depthValue= texture(uTexDepth, iVertexData.texCoords).r;

    vec3 albedo = texture(uTexAlbedo, iVertexData.texCoords).rgb;

    frag_out = vec4(albedo, 1.0);
}
