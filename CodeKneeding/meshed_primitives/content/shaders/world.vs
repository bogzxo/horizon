#version 460 core

// vertex data
layout(location = 0) in uint vPackedData;

uniform mat4 uCameraView;
uniform mat4 uCameraProjection;

uniform mat4 uSunViewProjNear;
uniform mat4 uSunViewProjFar;

uniform vec3 uCameraPosition;

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
    float distanceFromCamera = length(vs_out.fragPos - uCameraPosition);
    
    // Calculate light space coordinates
    vs_out.fragPosLightSpaceFar = uSunViewProjFar * vec4(vs_out.fragPos, 1.0);
    vs_out.fragPosLightSpaceNear = uSunViewProjNear * vec4(vs_out.fragPos, 1.0);
    // Select cascade based on distance
    
    vs_out.cascade = distanceFromCamera > 25.0 ? 1 : 0;
    
    // Transform the final position using camera view and projection matrices
    gl_Position = uCameraProjection * uCameraView * vec4(vs_out.fragPos, 1.0);
}
