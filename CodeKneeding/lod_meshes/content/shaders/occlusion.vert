#version 460 core

// vertex data
layout(location = 0) in uint vPackedData;

// layout(binding = 2, std430) readonly buffer CameraData {
//     mat4 view;
//     mat4 projection;
//     vec3 camPos;
// };

uniform mat4 view;
uniform mat4 projection;

layout (std430, binding = 1) buffer b_visibilityBuffer {
    int visibility[];
};

#include "world.h"

out flat int drawId;

void main() {
    drawId = gl_DrawID;
    
    // Transform the final position using camera view and projection matrices
    gl_Position = projection * view * vec4(getFinalPosition(), 1.0);
}
