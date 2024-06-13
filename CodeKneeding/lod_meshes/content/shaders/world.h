
struct chunkOffset {
	int xPos;
	int zPos;
    int face;
};

layout(binding = 0, std430) readonly buffer b_chunkOffsets {
  chunkOffset data[];
} chunkOffets;

#define ATLAS_SIZE 256.0
#define SINGLE_TILE_SIZE (16.0 / ATLAS_SIZE)
#define ATLAS_OFFSET 256.0 / 16.0

#define POS_BITS 0x3F

vec3 normals[6] = {
    vec3(0.0, -1.0, 0.0),
    vec3(0.0, 1.0, 0.0),
    vec3(0.0, 0.0, 1.0),
    vec3(0.0, 0.0, -1.0),
    vec3(1.0, 0.0, 0.0),
    vec3(-1.0, 0.0, 0.0)
};

vec3 getNormal() {
    return normals[chunkOffets.data[gl_DrawID].face];
}

vec3 getFinalPosition() {
    // Extracting x, y, and z from iPackedData (5 bits each)
    uint x = (vPackedData >> 4) & POS_BITS;
    uint y = (vPackedData >> 10) & POS_BITS;
    uint z = (vPackedData >> 16) & POS_BITS;

    return vec3(x, y, z) + vec3(chunkOffets.data[gl_DrawID].xPos, 0.0, chunkOffets.data[gl_DrawID].zPos);
}

vec2 getFinalTexCoords() {
    // extract the id
    float tileId = float((vPackedData >> 22) & 0xF);
    float x = float((vPackedData >> 26) & 0x1);
    float y = float((vPackedData >> 27) & 0x1);


    // Pass through data to fragment shader
    return vec2(x, y) * SINGLE_TILE_SIZE + vec2(mod(tileId, ATLAS_OFFSET), tileId / ATLAS_OFFSET) * SINGLE_TILE_SIZE;
}