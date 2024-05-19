#version 460 core

// vertex data
layout(location = 0) in uint vLocalPacked;
    
// instance data
layout(location = 1) in uint iPackedData;

uniform mat4 uCameraView;
uniform mat4 uCameraProjection;
uniform mat4 uSunView;
uniform mat4 uSunProjection;

layout(location = 0) out VS_OUT {
    vec2 texCoords; 
    vec3 fragPos;
    vec4 fragPosLightSpace;
    vec3 normal;
} vs_out;

#include "world.h"

void main() {
    vs_out.texCoords = getFinalTexCoords();
	vs_out.normal = getNormal();
    vs_out.fragPos = getFinalPosition();
    vs_out.fragPosLightSpace = uSunProjection * uSunView * vec4(vs_out.fragPos, 1.0);

    // Transform the final position using camera view and projection matrices
    gl_Position = uCameraProjection * uCameraView * vec4(vs_out.fragPos, 1.0);
}
