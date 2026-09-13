#version 460

layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vTexCoords;


uniform mat4 uCameraView;
uniform mat4 uCameraProjection;
uniform mat4 uModel;


uniform vec2 uSingleFrameSize;
uniform int uDataOffset;

struct SpriteData {
	mat4 modelMatrix;
	vec2 spriteOffset;
	uint frameIndex;
	uint span;
};



layout(std430) buffer spriteData
{
	SpriteData data[];
};


layout(location = 0) out vec2 oTexCoords;
layout(location = 1) out vec2 oFragPos;


void main() {
	// calculate texture coords with animation data.
	vec2 sprSize = uSingleFrameSize + vec2(uSingleFrameSize.x * data[gl_InstanceID + uDataOffset].span, 0);

	oTexCoords = data[gl_InstanceID + uDataOffset].spriteOffset + vTexCoords * sprSize + vec2(sprSize.x * data[gl_InstanceID + uDataOffset].frameIndex, 0);

	// Transform the vertex position
	vec4 worldPos = uModel * data[gl_InstanceID + uDataOffset].modelMatrix * vec4(vPos, 0.0, 1.0);
	gl_Position = uCameraProjection * uCameraView * worldPos;
	oFragPos = worldPos.xy;

}