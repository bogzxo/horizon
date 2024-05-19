#version 460 core

layout(location = 0) in VS_OUT {
    vec2 texCoords; 
    vec3 fragPos;
    vec4 fragPosLightSpace;
    vec3 normal;
} fs_in;

uniform sampler2D uTexAlbedo;
uniform vec3 uSunDirection;
uniform sampler2D uTexSunDepth;

layout(location = 0) out vec4 frag_out;

vec3 lightDir = vec3(0.3, 0.4, 0.5);
#define zNear 0.01
#define zFar 500.0 

float depthToLinear(float depthValue) {
    return (2.0 * zNear) / (zFar + zNear - depthValue * (zFar - zNear));
}

vec2 poissonDisk[4] = vec2[](
  vec2( -0.94201624, -0.39906216 ),
  vec2( 0.94558609, -0.76890725 ),
  vec2( -0.094184101, -0.92938870 ),
  vec2( 0.34495938, 0.29387760 )
);

int random(vec4 seed4) {
    float dot_product = dot(seed4, vec4(12.9898,78.233,45.164,94.673));
    return int(fract(sin(dot_product) * 43758.5453));
}

float ShadowCalculation(vec4 fragPosLightSpace)
{
    // perform perspective divide
    vec3 projCoords = fragPosLightSpace.xyz / fragPosLightSpace.w;
    
    // transform to [0,1] range
    projCoords = projCoords * 0.5 + 0.5;
    
     if(projCoords.z > 1.0)
         return 0.0;

    float bias = max(0.0005 * (1.0 - dot(fs_in.normal, uSunDirection)), 0.0005);  
    
    // get depth of current fragment from light's perspective
    float currentDepth = projCoords.z;
    //return currentDepth - closestDepth;
    
    float shadow = 0.2;

    for (int i=0;i<4;i++){
        int index = int(16.0*random(gl_FragCoord.xyyx * i))%16;
        float pcfDepth = texture(uTexSunDepth, projCoords.xy + poissonDisk[index] / 700.0).r; 
        if (pcfDepth > currentDepth - bias){
            shadow += 0.25;
        }
    }
    return shadow;  
}  

void main() {
    vec3 col = (texture(uTexAlbedo, fs_in.texCoords)).rgb * vec3(max(0.3, abs(dot(lightDir, fs_in.normal))) + 0.2);
    frag_out = vec4(col * (ShadowCalculation(fs_in.fragPosLightSpace)), 1.0);
}
