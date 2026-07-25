#version 460 core

layout(location = 0) in vec2 vPosition;
layout(location = 1) in vec2 vTexCoords;
layout(location = 2) in uint vId;

layout(location = 0) out vec2 oTexCoords;

uniform mat4 u_vp;
uniform mat4 u_model;

struct TextLabel {
  mat4 modelMatrix;
};

layout(std430) buffer labelData 
{
  TextLabel data[];
};

void main()
{
	oTexCoords = vTexCoords;
	gl_Position = u_vp * (u_model * data[vId].modelMatrix) * vec4(vPosition, 0.0, 1.0);
}