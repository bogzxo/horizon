// The tile map, GPU driven. Every tile of the map is an instance in one storage buffer, in chunks of a few hundred
// tiles that sit together, and every layer's settings are in a buffer of their own that the tile points at. The
// cull pass (tilemap_cull.slang) writes a draw for every chunk that is in view, and the map is one indirect draw
// of the one quad however many layers and tile sets it has. The textures come out of the bindless table.

#include <common/camera.slang>
#include <common/gbuffer.slang>
#include <common/quad.slang>

// Must match TileMapGpu.Layer in TileMapBatch.cs.
struct Layer
{
    // Where the layer is, the corner of the map, how far the layer is moved from it and what its parallax adds to that
    float2 offset;

    float2 padding0;
    float padding1;

    // How much of the layer shows no matter the light (a sky, a glowing sign)
    float emissive;
    float2 padding;

    // What the layer multiplies everything in it by, its tint, and in the alpha how much of it there is
    float4 tint;
};

// Must match TileMapGpu.Tile in TileMapBatch.cs (48 bytes). Where its middle is and how big it is, the part of the
// image it is cut out of (in pixels, left, top, right, bottom), how it is turned (radians) and turned over (a TileFlip),
// which layer it is on and the bindless slots of its image, normal map and specular map (NO_TEXTURE for none).
struct Tile
{
    float2 position;
    float2 size;
    float4 source;
    float rotation;
    float flip;
    uint layer;
    uint albedo;
    uint normal;
    uint specular;
    float2 texelSize;
};

BIND_BUFFER(2) StructuredBuffer<Layer> layers;
BIND_BUFFER(3) StructuredBuffer<Tile> tiles;
BINDLESS_TEXTURES(uTextures);

static const int FLIP_HORIZONTAL = 1;
static const int FLIP_VERTICAL = 2;
static const int FLIP_DIAGONAL = 4;

struct Varyings
{
    float4 position : SV_Position;
    float2 texCoords : TEXCOORD0;
    nointerpolation int flip : FLIP;
    nointerpolation uint layer : LAYER;
    nointerpolation uint albedo : ALBEDO;
    nointerpolation uint normal : NORMALMAP;
    nointerpolation uint specular : SPECULARMAP;
};

[shader("vertex")]
Varyings vsMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID, uint baseInstance : SV_StartInstanceLocation)
{
    Tile tile = tiles[quadInstance(instanceId, baseInstance)];
    Layer layer = layers[tile.layer];
    uint cornerIndex = quadCorner(vertexId);

    float c = cos(tile.rotation);
    float s = sin(tile.rotation);
    float2 corner = QUAD_POSITIONS[cornerIndex] * tile.size;
    float2 world = tile.position + float2(corner.x * c - corner.y * s, corner.x * s + corner.y * c) + layer.offset;

    // Tiled turns a tile over along its diagonal first, then sideways, then upside down. Finding the part of the image
    // that ends up at a corner is the same thing backwards
    int flip = int(tile.flip + 0.5);
    float2 at = QUAD_TEXCOORDS[cornerIndex];
    if ((flip & FLIP_VERTICAL) != 0) at.y = 1.0 - at.y;
    if ((flip & FLIP_HORIZONTAL) != 0) at.x = 1.0 - at.x;
    if ((flip & FLIP_DIAGONAL) != 0) at = at.yx;

    Varyings output;
    output.flip = flip;
    output.layer = tile.layer;
    output.albedo = tile.albedo;
    output.normal = tile.normal;
    output.specular = tile.specular;
    output.texCoords = lerp(tile.source.xy, tile.source.zw, at) * tile.texelSize;
    output.position = toClip(float4(world, 0.0, 1.0));
    return output;
}

[shader("fragment")]
GBuffer fsMain(Varyings input)
{
    Layer layer = layers[input.layer];

    float4 albedo = uTextures[NonUniformResourceIndex(input.albedo)].Sample(input.texCoords) * layer.tint;
    if (albedo.a <= 0.0) discard;

    // The way a surface faces turns over along with the tile it is on, or a tile that is mirrored would be lit from
    // the wrong side. The normal map has Y going up and the image has it going down, which is where the minus of the
    // diagonal comes from
    float2 normal = input.normal != NO_TEXTURE ? uTextures[NonUniformResourceIndex(input.normal)].Sample(input.texCoords).xy : float2(0.5, 0.5);
    if ((input.flip & FLIP_DIAGONAL) != 0) normal = float2(1.0, 1.0) - normal.yx;
    if ((input.flip & FLIP_HORIZONTAL) != 0) normal.x = 1.0 - normal.x;
    if ((input.flip & FLIP_VERTICAL) != 0) normal.y = 1.0 - normal.y;

    float shine = input.specular != NO_TEXTURE ? uTextures[NonUniformResourceIndex(input.specular)].Sample(input.texCoords).r : 0.0;

    GBuffer output;
    output.albedo = albedo;
    output.surface = float4(normal, layer.emissive, albedo.a);
    output.material = float4(shine, 0.0, layer.emissive, albedo.a);
    return output;
}
