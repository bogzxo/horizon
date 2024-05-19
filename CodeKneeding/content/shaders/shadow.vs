#version 460 core

// vertex data
layout(location = 0) in uint vLocalPacked;
    
// instance data
layout(location = 1) in uint iPackedData;

uniform mat4 uCameraView;
uniform mat4 uCameraProjection;


#include "world.h"

void main() {
    // Transform the final position using camera view and projection matrices
    gl_Position = uCameraProjection * uCameraView * vec4(getFinalPosition(), 1.0);
}
