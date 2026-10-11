// The one quad everything that is drawn as instances of a quad is drawn with, the sprites, the shapes and the
// tiles. A square of one by one around the origin (-0.5 to 0.5) with the texture coordinates of an image that has
// its first row at the top, two triangles, six vertices. It has no vertex buffer at all, the corners come out of
// the vertex number, so a draw of it is DrawInstanced(Triangles, 6, count) and nothing else has to be bound.

static const float2 QUAD_POSITIONS[4] = { float2(-0.5, -0.5), float2(0.5, -0.5), float2(0.5, 0.5), float2(-0.5, 0.5) };
static const float2 QUAD_TEXCOORDS[4] = { float2(0.0, 1.0), float2(1.0, 1.0), float2(1.0, 0.0), float2(0.0, 0.0) };
static const uint QUAD_INDICES[6] = { 0, 1, 2, 0, 2, 3 };

// Which corner of the quad a vertex of the draw is.
uint quadCorner(uint vertexId)
{
    return QUAD_INDICES[vertexId % 6];
}

// The instance the draw is on, counted from the first instance of the frame's buffer rather than of the draw.
uint quadInstance(uint instanceId, uint baseInstance)
{
    return instanceId + baseInstance;
}

// A colour packed into a uint, red in the low byte, back to 0 to 1.
float4 unpackColor(uint packed)
{
    return float4(packed & 0xFFu, (packed >> 8) & 0xFFu, (packed >> 16) & 0xFFu, packed >> 24) / 255.0;
}
