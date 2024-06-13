#version 460 core

layout(location = 0) in vec2 iPosition;
layout(location = 1) in vec2 iTexCoords;

#include "post.h"

layout(location=0) out VertexData oVertexData;

void main() {
    oVertexData.texCoords = iTexCoords;;
    gl_Position = vec4(iPosition, 0.0, 1.0);
}
