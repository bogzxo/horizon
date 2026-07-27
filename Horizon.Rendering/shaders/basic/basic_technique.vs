#version 410 core

layout(location = 0) in vec2 vPosition;
layout(location = 0) in vec3 vColour;

uniform mat4 uCameraView;
uniform mat4 uCameraProjection;

out vec3 colour;

void main()
{
	colour = vColour;
	gl_Position = uCameraProjection  * uCameraView * vec4(vPosition, 0.0, 1.0);
}