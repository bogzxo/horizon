#version 460

layout(location = 0) in vec2 vPos;
layout(location = 1) in vec2 vTexCoords;


uniform mat4 uCameraView;
uniform mat4 uCameraProjection;
uniform mat4 uModel;

uniform int uDataOffset;

// Must match SpriteItem.cs.
struct SpriteItem {
	vec2 origin;
	vec2 axisX;
	vec2 axisY;
	vec2 texMin;
	vec2 texMax;
	uint color;
	uint flags;
	float depth;
	float padding;
};



layout(std430) buffer spriteData
{
	SpriteItem data[];
};


layout(location = 0) out vec2 oTexel;
layout(location = 1) out vec2 oFragPos;
layout(location = 2) out vec4 oColor;
layout(location = 3) flat out uint oFlags;


void main() {
	SpriteItem item = data[gl_InstanceID + uDataOffset];

	// the quad goes from -0.5 to 0.5, the item is described from its bottom left corner.
	vec2 corner = vPos + vec2(0.5);

	// texMin is what the top left corner shows, so V runs against Y. Left in texels: the fragment shader
	// knows which texture they are of.
	oTexel = mix(item.texMin, item.texMax, vec2(corner.x, 1.0 - corner.y));
	oColor = unpackUnorm4x8(item.color);
	oFlags = item.flags;

	// Transform the vertex position
	vec4 worldPos = uModel * vec4(item.origin + item.axisX * corner.x + item.axisY * corner.y, item.depth, 1.0);
	gl_Position = uCameraProjection * uCameraView * worldPos;
	oFragPos = worldPos.xy;

}
