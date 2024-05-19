
struct chunkOffset {
	int xPos;
	int zPos;
};

layout(std430) buffer b_chunkOffsets {
  chunkOffset data[];
} chunkOffets;

#define ATLAS_SIZE 256.0
#define SINGLE_TILE_SIZE (16.0 / ATLAS_SIZE)
#define ATLAS_OFFSET 256.0 / 16.0

vec3 getPosition() {
     // Extracting X, Z coordinates
    float xCoord = float((vLocalPacked >> 0) & 0x01); // Extracting first bit
    float zCoord = float((vLocalPacked >> 1) & 0x01); // Extracting second bit

    return vec3(xCoord, 0, zCoord);
}

vec2 getTexCoords() {
    // Extracting texture coordinates
    float texXCoord = float((vLocalPacked >> 0) & 0x01); // Extracting third bit
    float texYCoord = float((vLocalPacked >> 1) & 0x01); // Extracting fourth bit

    return vec2(texXCoord, texYCoord);
}

vec3 normals[6] = {
    vec3(0.0, -1.0, 0.0),
    vec3(0.0, 1.0, 0.0),
    vec3(0.0, 0.0, 1.0),
    vec3(0.0, 0.0, -1.0),
    vec3(1.0, 0.0, 0.0),
    vec3(-1.0, 0.0, 0.0)
};

uint getFace() {
    return (iPackedData >> 0) & 0xF;
}

vec3 getNormal() {
    return normals[getFace()];
}

vec3 getFinalPosition() {
    vec3 position = getPosition();
    uint face = getFace();
    if (face == 1) {
        position.y++;
    }
    else if (face == 2) {
        position.xzy = position.xyz;
    }
    else if (face == 3) {
        position.xzy = position.xyz;
        position.z++;
    }
    else if (face == 4) {
        position.yxz = position.xyz;
    }
    else if (face == 5) {
        position.yxz = position.xyz;
        position.x++;
    }

    // Extracting x, y, and z from iPackedData (5 bits each)
    uint x = (iPackedData >> 4) & 0x1F;
    uint y = (iPackedData >> 9) & 0x1F;
    uint z = (iPackedData >> 14) & 0x1F;

    return vec3(x, y, z) + position + vec3(chunkOffets.data[gl_DrawID].xPos, 0.0, chunkOffets.data[gl_DrawID].zPos);
}

vec2 getFinalTexCoords() {
    // extract the id
    float tileId = float((iPackedData >> 19) & 0xF);

    // Pass through data to fragment shader
    return getTexCoords() * SINGLE_TILE_SIZE + vec2(mod(tileId, ATLAS_OFFSET), tileId / ATLAS_OFFSET) * SINGLE_TILE_SIZE;
}