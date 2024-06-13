#version 460 core

// vertex data
layout(location = 0) in uint vPackedData;

layout(binding = 1, std430) readonly buffer CameraData {
    mat4 view;
    mat4 projection;
    vec3 camPos;
};

uniform mat4 uSunViewProjNear;
uniform mat4 uSunViewProjFar;

layout(location = 0) out VS_OUT {
    vec2 texCoords; 
    vec3 fragPos;
    vec4 fragPosLightSpaceFar;
    vec4 fragPosLightSpaceNear;
    vec3 normal;
    flat int cascade;
} vs_out;

#include "world.h"

void main() {
    vs_out.texCoords = getFinalTexCoords();
    vs_out.normal = getNormal();
    vs_out.fragPos = getFinalPosition();
    
    // Calculate distance from camera to fragment position
    float distanceFromCamera = length(vs_out.fragPos - camPos);
    
    // Calculate light space coordinates
    vs_out.fragPosLightSpaceFar = uSunViewProjFar * vec4(vs_out.fragPos, 1.0);
    vs_out.fragPosLightSpaceNear = uSunViewProjNear * vec4(vs_out.fragPos, 1.0);
    // Select cascade based on distance
    
    vs_out.cascade = distanceFromCamera > 25.0 ? 1 : 0;
    
    // Transform the final position using camera view and projection matrices
    gl_Position = projection * view * vec4(vs_out.fragPos, 1.0);
}
