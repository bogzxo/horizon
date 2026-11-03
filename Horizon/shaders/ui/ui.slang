// The UI's own shader, what UIRenderer draws with. The sprite renderer's quads (see spritebatch/sprites.slang, the
// item is the same 64 bytes) without what only lit sprites want, flashes, lamps and shadows cast, and with shapes of
// its own. A rounded box, the edge of one and the soft shadow under one are a quad each here, worked out from how far
// the pixel is from the shape's edge, where on the sprite renderer a box was seven quads and its edge eight. A shape
// is clipped here as well, a quad cut down on the CPU would have cut its corners off with it.

#include <common/camera.slang>
#include <common/gbuffer.slang>
#include <common/quad.slang>

struct Params
{
    // What the whole batch is moved by
    float4x4 uModel;
};

BIND_UNIFORM(1) ConstantBuffer<Params> params;

// Must match SpriteItem.cs. A shape (SHAPE_FLAG) has its own use for some of it, see below
struct SpriteItem
{
    float2 origin;
    float2 axisX;
    float2 axisY;
    float2 texMin;
    float2 texMax;
    float2 padding;
    uint color;
    uint flags;
    float depth;
    float ring;
};

BIND_BUFFER(3) StructuredBuffer<SpriteItem> items;

BINDLESS_TEXTURES(uTextures);

// Must match SpriteItem.cs. The texture is the low sixteen bits of the flags, its slot in the bindless table.
static const uint TEXTURE_MASK = 0xFFFFu;
static const uint COVERAGE_FLAG = 0x10000u;
static const uint SMOOTH_FLAG = 0x20000u;
static const uint CORNER_FLAG = 0x40000u;
static const uint FIELD_FLAG = 0x200000u;
static const uint BOLD_FLAG = 0x800000u;
static const uint SOFT_FLAG = 0x1000000u;

// A rounded box, the edge of one or the shadow of one, worked out per pixel. Its ring is the radius of its corners in
// pixels (for a shadow how far it fades), texMin.x how thick its edge is (0 for a box filled in), texMin.y 1 for a
// shadow, and padding and texMax the clip, its bottom left and top right corner on the screen. Must match UIDrawList
static const uint SHAPE_FLAG = 0x2000000u;

// How far out into the field the edge of a bold glyph is, as a share of the field's value at the edge. Must match
// UIFont.BOLD_GROWTH, which widens every bold letter by as much as the ink grows
static const float BOLD_GROWTH = 0.2;

struct Varyings
{
    float4 position : SV_Position;
    float2 texel : TEXEL;
    float2 fragPos : FRAGPOS;
    float4 color : COLOR;
    nointerpolation uint flags : FLAGS;
    nointerpolation float ring : RING;
    nointerpolation float2 texelSize : TEXELSIZE;

    // For a shape, where the pixel is from its middle and how big half of it is, in pixels, and its clip
    float2 local : LOCAL;
    nointerpolation float2 half : HALF;
    nointerpolation float4 clip : CLIP;
    nointerpolation float2 shape : SHAPE;
};

[shader("vertex")]
Varyings vsMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID, uint baseInstance : SV_StartInstanceLocation)
{
    SpriteItem item = items[quadInstance(instanceId, baseInstance)];
    uint cornerIndex = quadCorner(vertexId);

    // The quad goes from -0.5 to 0.5, the item is described from its bottom left corner
    float2 corner = QUAD_POSITIONS[cornerIndex] + 0.5;

    Varyings output;
    output.texel = lerp(item.texMin, item.texMax, float2(corner.x, 1.0 - corner.y));
    output.color = unpackColor(item.color);
    output.flags = item.flags;
    output.ring = item.ring;

    uint texture = item.flags & TEXTURE_MASK;
    output.texelSize = float2(0.0, 0.0);
    if (texture != NO_TEXTURE)
    {
        uint width, height;
        uTextures[NonUniformResourceIndex(texture)].GetDimensions(width, height);
        output.texelSize = 1.0 / float2(max(width, 1u), max(height, 1u));
    }

    float2 size = float2(length(item.axisX), length(item.axisY));
    output.half = size * 0.5;
    output.local = (corner - 0.5) * size;
    output.clip = float4(item.padding, item.texMax);
    output.shape = item.texMin;

    float4 world = mul(float4(item.origin + item.axisX * corner.x + item.axisY * corner.y, item.depth, 1.0), params.uModel);
    output.position = toClip(world);
    output.fragPos = world.xy;
    return output;
}

// See sprites.slang, the pixel art filter
float4 smoothTexel(Sampler2D tex, float2 at, float2 box)
{
    float2 corner = at - 0.5 * box;
    float2 blend = clamp((frac(corner) - (1.0 - box)) / box, 0.0, 1.0);

    uint width, height;
    tex.GetDimensions(width, height);
    int2 first = int2(floor(corner));
    int2 last = int2(width, height) - 1;

    float4 a = tex.Load(int3(clamp(first, int2(0, 0), last), 0));
    float4 b = tex.Load(int3(clamp(first + int2(1, 0), int2(0, 0), last), 0));
    float4 c = tex.Load(int3(clamp(first + int2(0, 1), int2(0, 0), last), 0));
    float4 d = tex.Load(int3(clamp(first + int2(1, 1), int2(0, 0), last), 0));

    a.rgb *= a.a;
    b.rgb *= b.a;
    c.rgb *= c.a;
    d.rgb *= d.a;

    float4 result = lerp(lerp(a, b, blend.x), lerp(c, d, blend.x), blend.y);
    if (result.a > 0.0) result.rgb /= result.a;
    return result;
}

// How far a point is outside a box with rounded corners (less than 0 inside it), the box half as big as half either way
float roundBox(float2 p, float2 half, float radius)
{
    float2 q = abs(p) - half + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

[shader("fragment")]
GBuffer fsMain(Varyings input)
{
    uint texture = input.flags & TEXTURE_MASK;

    // Worked out up front, outside of any branch, so the derivatives are always well defined (see sprites.slang)
    float2 box = clamp(fwidth(input.texel), float2(0.00001, 0.00001), float2(1.0, 1.0));
    float fromMiddle = length(input.texel);
    float cornerPixel = max(fwidth(fromMiddle), 0.00001);
    float2 uv = input.texel * input.texelSize;
    float2 uvAcross = ddx(uv), uvDown = ddy(uv);
    float texelsAPixel = max(length(ddx(input.texel)), length(ddy(input.texel)));

    float4 tex = float4(1.0, 1.0, 1.0, 1.0);
    if ((input.flags & SHAPE_FLAG) != 0u)
    {
        // Cut off where its clip is, the way a quad is trimmed on the CPU, by whether the middle of the pixel is in it
        if (any(input.fragPos < input.clip.xy) || any(input.fragPos > input.clip.zw))
            discard;

        if (input.shape.y > 0.0)
        {
            // A shadow, solid inside of a box as far in from its edge as it fades and fading out from there, eased so
            // it has no line where it starts. The same as the soft discs and edges it was made of
            float reach = max(input.ring, 0.0001);
            float2 inner = max(input.half - reach, 0.0);
            float t = saturate(1.0 - length(max(abs(input.local) - inner, 0.0)) / reach);
            tex.a = t * t * (3.0 - 2.0 * t);
        }
        else
        {
            // In the box by half a pixel either side of its edge, and for an edge outside of the box inside of it
            float outside = roundBox(input.local, input.half, input.ring);
            tex.a = saturate(0.5 - outside);
            float thickness = input.shape.x;
            if (thickness > 0.0)
            {
                float hole = roundBox(input.local, max(input.half - thickness, 0.0), max(input.ring - thickness, 0.0));
                tex.a *= saturate(0.5 + hole);
            }
        }
    }
    else if (texture != NO_TEXTURE)
    {
        tex = (input.flags & SMOOTH_FLAG) != 0u && (input.flags & FIELD_FLAG) == 0u
            ? smoothTexel(uTextures[NonUniformResourceIndex(texture)], input.texel, box)
            : uTextures[NonUniformResourceIndex(texture)].SampleGrad(uv, uvAcross, uvDown);
    }

    // The texture only says where the ink is (fonts), the colour is all the item's
    if ((input.flags & COVERAGE_FLAG) != 0u) tex = float4(1.0, 1.0, 1.0, tex.a);

    // Text out of a distance field, four samples to the pixel (see sprites.slang for why)
    float field = tex.r;
    float fieldPixel = max(fwidth(field), 0.0001);
    if ((input.flags & FIELD_FLAG) != 0u)
    {
        float pixel = input.ring > 0.0 ? max(input.ring * texelsAPixel, 0.0001) : fieldPixel;
        float edge = pixel * 0.5;
        float middle = (input.flags & BOLD_FLAG) != 0u ? 0.5 - 0.5 * BOLD_GROWTH : 0.5;

        float ink = 0.0;
        ink += clamp((uTextures[NonUniformResourceIndex(texture)].SampleLevel(uv + uvAcross * 0.125 + uvDown * 0.375, 0.0).r - middle) / edge + 0.5, 0.0, 1.0);
        ink += clamp((uTextures[NonUniformResourceIndex(texture)].SampleLevel(uv - uvAcross * 0.375 + uvDown * 0.125, 0.0).r - middle) / edge + 0.5, 0.0, 1.0);
        ink += clamp((uTextures[NonUniformResourceIndex(texture)].SampleLevel(uv + uvAcross * 0.375 - uvDown * 0.125, 0.0).r - middle) / edge + 0.5, 0.0, 1.0);
        ink += clamp((uTextures[NonUniformResourceIndex(texture)].SampleLevel(uv - uvAcross * 0.125 - uvDown * 0.375, 0.0).r - middle) / edge + 0.5, 0.0, 1.0);

        tex = float4(1.0, 1.0, 1.0, ink * 0.25);
    }

    // A quarter of a disc, what's left drawing them (the silver lining of glass), see sprites.slang
    if ((input.flags & CORNER_FLAG) != 0u && (input.flags & SOFT_FLAG) != 0u)
    {
        float t = saturate(1.0 - fromMiddle);
        tex.a *= t * t * (3.0 - 2.0 * t);
    }
    else if ((input.flags & CORNER_FLAG) != 0u)
    {
        float inside = clamp((1.0 - fromMiddle) / cornerPixel + 0.5, 0.0, 1.0);
        float pastHole = input.ring < 1.0 ? clamp((fromMiddle - (1.0 - input.ring)) / cornerPixel + 0.5, 0.0, 1.0) : 1.0;
        tex.a *= inside * pastHole;
    }

    float4 result = tex * input.color;
    if (result.a <= 0.0) discard;

    // Flat, the UI isn't lit, and wide open to the ambient light the way a sprite that casts no shadow is
    GBuffer output = writeFlat(result, 0.0);
    output.material.g = packOcclusion(1.0);
    output.material.b = 0.0;
    return output;
}
