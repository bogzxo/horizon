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
layout(location = 2) out vec3 oNormal;
layout(location = 1) out float oDrawId;

struct chunkOffset {
	int xPos;
	int yPos;
	int face;
};

layout(std430) buffer b_chunkOffsets {
  chunkOffset data[];
} chunkOffets;


void main() {
    // Pass through data to fragment shader
    oTexCoords = vTexCoords;
    oDrawId = float(gl_DrawID) / 8192.0;

    // Extracting face from iPackedData (4 bits)
    int face = chunkOffets.data[gl_DrawID].face;

    // Extracting x, y, and z from iPackedData (5 bits each)
    int x = (iPackedData >> 4) & 0x1F;
    int y = (iPackedData >> 9) & 0x1F;
    int z = (iPackedData >> 14) & 0x1F;

    // Compute the offset based on the extracted x, y, and z values
    vec3 aOffset = vec3(x, y, z);

    // Compute the position based on the face value
    vec3 position = vPosition;
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

    // Compute the normal vector based on the face value
    vec3 normal;
    if (face == 0) {
        normal = vec3(0.0, -1.0, 0.0); // Bottom face
    }
    else if (face == 1) {
        normal = vec3(0.0, 1.0, 0.0); // Top face
    }
    else if (face == 2) {
        normal = vec3(0.0, 0.0, 1.0); // Front face
    }
    else if (face == 3) {
        normal = vec3(0.0, 0.0, -1.0); // Back face
    }
    else if (face == 4) {
        normal = vec3(1.0, 0.0, 0.0); // Right face
    }
    else if (face == 5) {
        normal = vec3(-1.0, 0.0, 0.0); // Left face
    }

	oNormal = normal;

    // Combine the position, offset, and normal to compute the final vertex position
    vec3 finalPosition = vec3(chunkOffets.data[gl_DrawID].xPos, 0.0, chunkOffets.data[gl_DrawID].yPos) + position + aOffset;

    // Transform the final position using camera view and projection matrices
    gl_Position = uCameraProjection * uCameraView * vec4(finalPosition, 1.0);
}
