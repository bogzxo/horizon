#version 460 core

// vertex data
layout(location = 0) in vec3 vPosition;
layout(location = 1) in vec2 vTexCoords;

// instance data
layout(location = 2) in int iPackedData;
// layout(location = 2) in vec3 iPos;
// layout(location = 3) in int iFace;

uniform mat4 uCameraView;
uniform mat4 uCameraProjection;

layout(location = 0) out vec2 oTexCoords;
layout(location = 1) out float drawId;

struct chunkOffset {
	int xPos;
	int yPos;
	int spacer0;
	int spacer1;
};

layout(std430) buffer b_chunkOffsets {
  chunkOffset data[];
} chunkOffets;


void main() {
	// pas through data to fs
	oTexCoords = vTexCoords;
	drawId = gl_DrawID / 4.0f;
	// unpack position offset in ranges 0 - 31
	vec3 aOffset = vec3(iPackedData & 31, (iPackedData >> 5) & 31, (iPackedData >> 10) & 31);
	//vec3 aOffset = iPos;
	// transform vertices to match the correct face orientation
	vec3 position = vPosition;
	int face = (iPackedData >> 15) & 6;
	//int face = iFace;

	if (face == 1) {
		position.y++;
	}
	else if (face == 2) {
		position.xy = position.yx;
		position.x++;
	}
	else if (face == 3) {
		position.xy = position.yx;
	}
	else if (face == 4) {
		position.xzy = position.xyz;
		position.z++;
	}
	else if (face == 5) {
		position.xzy = position.xyz;
	}

	gl_Position = uCameraProjection * uCameraView * vec4(position + aOffset + vec3(chunkOffets.data[gl_DrawID].xPos, 0, chunkOffets.data[gl_DrawID].yPos), 1.0);
} 