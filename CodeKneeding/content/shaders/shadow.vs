#version 460 core

// vertex data
layout(location = 0) in uint vPackedData;

// layout(std430) buffer CameraData {
//     mat4 view;
//     mat4 projection;
//     vec3 camPos;
// };

uniform mat4 view;
uniform mat4 projection;

#include "world.h"

void main() {
    // Transform the final position using camera view and projection matrices
    gl_Position = projection * view * vec4(getFinalPosition(), 1.0);
}
