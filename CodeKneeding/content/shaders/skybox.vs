#version 460 core

layout(location = 0) in vec2 iPosition;
layout(location = 1) in vec2 iTexCoords;

layout(location=0) out vec3 oCamDir;

uniform mat4 uInvProj;
uniform mat4 uInvView;

void main() {
    gl_Position = vec4(iPosition, 0.0, 1.0);
    oCamDir = mat3(uInvView) * (uInvProj * gl_Position).xyz;
}
