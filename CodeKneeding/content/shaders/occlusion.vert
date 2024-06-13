#version 460 core

// vertex data
layout(location = 0) in uint vPackedData;

layout(binding = 3, std430) readonly buffer CameraData {
    mat4 view;
    mat4 projection;
    vec3 camPos;
};

layout (std430, binding = 2) buffer b_visibilityBuffer {
    int visibility[];
};

#include "world.h"

out flat int drawId;

void main() {
    drawId = gl_DrawID;
    
    // Transform the final position using camera view and projection matrices
    gl_Position = projection * view * vec4(getFinalPosition(), 1.0);
}
