#version 460 core
layout(early_fragment_tests) in;

layout (std430, binding = 1) buffer b_visibilityBuffer {
    int visibility[];
};

in flat int drawId;

void main() {
    visibility[drawId] = 1;
}
